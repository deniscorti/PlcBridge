namespace Bridge.Core.Model;

/// <summary>
/// Definition of a single tag within a DataSource.
/// </summary>
public sealed class Tag
{
    public required string Name { get; init; }
    public required DataKind Kind { get; init; }

    /// <summary>ADS address (e.g. "MAIN.fTemperature"). Null for UDP/Mock inputs.</summary>
    public string? Address { get; init; }

    /// <summary>Polling interval in ms. Only meaningful for Telemetry tags with ADS input.</summary>
    public int? PollMs { get; init; }

    /// <summary>CRC32 of the tag name, used as compact identifier in UDP protocol.</summary>
    public uint TagId { get; internal set; }
}
