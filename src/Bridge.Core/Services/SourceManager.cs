using System.Collections.Concurrent;
using Bridge.Core.Abstractions;
using Bridge.Core.Model;

namespace Bridge.Core.Services;

/// <summary>
/// Central registry for DataSources, their inputs, and inter-bridge WS clients.
/// Manages lifecycle and dispatches incoming values.
/// </summary>
public sealed class SourceManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DataSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<IDataInput>> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _wsClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISubscriptionBroker _broker;
    private AlarmService? _alarmService;

    /// <summary>Fired when a new TagValue arrives from any input.</summary>
    public event Action<TagValue>? OnValue;

    public SourceManager(ISubscriptionBroker broker)
    {
        _broker = broker;
    }

    public void SetAlarmService(AlarmService service) => _alarmService = service;
    public AlarmService? GetAlarmService() => _alarmService;

    public DataSource RegisterSource(string id)
    {
        var ds = new DataSource { Id = id };
        if (!_sources.TryAdd(id, ds))
            throw new InvalidOperationException($"DataSource '{id}' already registered.");
        return ds;
    }

    /// <summary>Register or get existing source (for inter-bridge where source is auto-created).</summary>
    public DataSource GetOrRegisterSource(string id)
    {
        return _sources.GetOrAdd(id, _ => new DataSource { Id = id });
    }

    public DataSource? GetSource(string id) => _sources.GetValueOrDefault(id);
    public IReadOnlyCollection<DataSource> GetAllSources() => _sources.Values.ToList();

    public void AddInput(IDataInput input)
    {
        var list = _inputs.GetOrAdd(input.SourceId, _ => new List<IDataInput>());
        lock (list) { list.Add(input); }
        input.OnValue += HandleValue;
    }

    /// <summary>Register a WsClient for inter-bridge connection (stored as object to avoid circular dependency).</summary>
    public void RegisterWsClient(string sourceId, object client)
    {
        _wsClients[sourceId] = client;
    }

    /// <summary>Get inter-bridge WS client for a source (returns dynamic to avoid dependency on InterBridge project).</summary>
    public dynamic? GetWsClient(string sourceId)
    {
        return _wsClients.TryGetValue(sourceId, out var client) ? client : null;
    }

    public async Task StartAllAsync(CancellationToken ct = default)
    {
        foreach (var (_, inputs) in _inputs)
            foreach (var input in inputs)
                await input.StartAsync(ct);
    }

    public async Task StopAllAsync()
    {
        foreach (var (_, inputs) in _inputs)
            foreach (var input in inputs)
                await input.StopAsync();
    }

    public IDataInput? GetInputForSource(string sourceId)
    {
        if (!_inputs.TryGetValue(sourceId, out var list)) return null;
        lock (list) { return list.FirstOrDefault(); }
    }

    public void HandleValue(TagValue value)
    {
        if (_sources.TryGetValue(value.Source, out var source))
            source.SetLastValue(value);

        _alarmService?.ProcessValue(value);
        OnValue?.Invoke(value);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, inputs) in _inputs)
            foreach (var input in inputs)
                await input.DisposeAsync();

        foreach (var (_, client) in _wsClients)
        {
            if (client is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }
}
