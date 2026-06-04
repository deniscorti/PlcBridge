using Bridge.Core.Model;

namespace Bridge.Core.Abstractions;

/// <summary>
/// Represents a data input source (ADS, UDP, Modbus, OPC-UA, Mock, etc.).
/// Produces TagValue items and optionally supports read/write commands.
/// </summary>
public interface IDataInput : IAsyncDisposable
{
    /// <summary>DataSource Id this input feeds.</summary>
    string SourceId { get; }

    /// <summary>Protocol/driver name (e.g. "udp", "ads", "modbus", "mock").</summary>
    string Protocol { get; }

    /// <summary>Capabilities supported by this input.</summary>
    InputCapabilities Capabilities { get; }

    /// <summary>Whether the input is currently connected/active.</summary>
    bool IsConnected { get; }

    /// <summary>Start the input (connect, begin polling/listening).</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop the input gracefully.</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>Read the current value of a tag directly from the source. Throws NotSupportedException if !Capabilities.HasFlag(Read).</summary>
    Task<object?> ReadTagAsync(string tag, CancellationToken ct = default);

    /// <summary>Write a value to a tag on the source. Throws NotSupportedException if !Capabilities.HasFlag(Write).</summary>
    Task WriteTagAsync(string tag, object value, CancellationToken ct = default);

    /// <summary>Fired when a new value is received from the input.</summary>
    event Action<TagValue>? OnValue;

    /// <summary>Fired when connection state changes (connected/disconnected).</summary>
    event Action<string, bool>? OnConnectionChanged;
}

/// <summary>
/// Capabilities that a data input driver can declare.
/// </summary>
[Flags]
public enum InputCapabilities
{
    None = 0,

    /// <summary>Can receive/push data from the source.</summary>
    Receive = 1,

    /// <summary>Can read a tag value on demand.</summary>
    Read = 2,

    /// <summary>Can write a tag value to the source.</summary>
    Write = 4,

    /// <summary>Supports subscription-based push (vs. polling).</summary>
    Subscribe = 8,

    /// <summary>Supports batch read of multiple tags at once.</summary>
    BatchRead = 16,

    /// <summary>Supports browsing available tags from the source.</summary>
    Browse = 32,
}

/// <summary>
/// Factory for creating data input instances from configuration.
/// Implement this to register new input protocols (Modbus, OPC-UA, etc.).
/// </summary>
public interface IDataInputFactory
{
    /// <summary>Protocol name this factory handles (e.g. "modbus", "ads", "opcua").</summary>
    string Protocol { get; }

    /// <summary>Create an input instance from configuration.</summary>
    IDataInput Create(DataSource source, IDataInputConfig config);
}

/// <summary>
/// Marker interface for input-specific configuration.
/// Each protocol implements its own config class.
/// </summary>
public interface IDataInputConfig
{
    /// <summary>Protocol type name (e.g. "udp", "ads", "modbus").</summary>
    string Type { get; }
}
