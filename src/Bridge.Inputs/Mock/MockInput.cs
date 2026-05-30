using Bridge.Core.Abstractions;
using Bridge.Core.Model;

namespace Bridge.Inputs.Mock;

/// <summary>
/// Generates fake data for testing without a physical PLC.
/// Produces telemetry (sine wave), events (random toggles), and alarms (random triggers).
/// </summary>
public sealed class MockInput : IDataInput
{
    private readonly DataSource _source;
    private readonly Random _rng = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public string SourceId => _source.Id;
    public string Protocol => "mock";
    public InputCapabilities Capabilities => InputCapabilities.Receive | InputCapabilities.Read;
    public bool IsConnected { get; private set; }
    public event Action<TagValue>? OnValue;
    public event Action<string, bool>? OnConnectionChanged;

    public MockInput(DataSource source)
    {
        _source = source;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsConnected = true;
        OnConnectionChanged?.Invoke(SourceId, true);
        _runTask = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        IsConnected = false;
        OnConnectionChanged?.Invoke(SourceId, false);
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_runTask is not null)
            {
                try { await _runTask; } catch (OperationCanceledException) { }
            }
            _cts.Dispose();
        }
    }

    public Task<object?> ReadTagAsync(string tag, CancellationToken ct = default)
    {
        var tagDef = _source.GetTag(tag);
        if (tagDef is null) throw new KeyNotFoundException($"Tag '{tag}' not found in source '{SourceId}'.");
        object? value = tagDef.Kind switch
        {
            DataKind.Telemetry => GenerateTelemetryValue(tag),
            DataKind.Event => _rng.Next(2) == 1,
            DataKind.Alarm => _rng.Next(2) == 1,
            _ => null
        };
        return Task.FromResult(value);
    }

    public Task WriteTagAsync(string tag, object value, CancellationToken ct = default)
    {
        // Mock: just accept the write, no actual PLC
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (name, tag) in _source.Tags)
            {
                var interval = tag.PollMs ?? 1000;
                if (tick % (interval / 100) != 0 && tag.Kind == DataKind.Telemetry) continue;

                object? value = tag.Kind switch
                {
                    DataKind.Telemetry => GenerateTelemetryValue(name, tick),
                    DataKind.Event when _rng.Next(50) == 0 => _rng.Next(2) == 1,
                    DataKind.Alarm when _rng.Next(100) == 0 => _rng.Next(2) == 1,
                    _ => null
                };

                if (value is null) continue;

                var tv = new TagValue
                {
                    Source = SourceId,
                    Tag = name,
                    Kind = tag.Kind,
                    Value = value,
                    Timestamp = now,
                    MsgId = _source.NextMsgId()
                };
                OnValue?.Invoke(tv);
            }

            tick++;
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private double GenerateTelemetryValue(string tagName, int tick = 0)
    {
        var hash = tagName.GetHashCode();
        var phase = (hash % 100) / 100.0 * Math.PI * 2;
        return 20.0 + 10.0 * Math.Sin(tick * 0.05 + phase) + (_rng.NextDouble() - 0.5) * 0.5;
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
