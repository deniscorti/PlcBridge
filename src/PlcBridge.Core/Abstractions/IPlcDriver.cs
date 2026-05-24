namespace PlcBridge.Core.Abstractions;

public interface IPlcDriver : IAsyncDisposable
{
    string PlcId { get; }
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task<object?> ReadAsync(string tag, CancellationToken ct = default);
    Task WriteAsync(string tag, object value, CancellationToken ct = default);
}
