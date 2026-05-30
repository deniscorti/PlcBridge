using System.Text.Json;
using Bridge.Core.Abstractions;
using Bridge.Core.Model;
using Bridge.Core.Services;
using Microsoft.Extensions.Logging;

namespace Bridge.InterBridge.WsClient;

/// <summary>
/// Aggregates downstream client subscriptions and propagates them as a single
/// upstream subscription set per source. Handles debouncing, ref-counting,
/// "ALL" short-circuiting, and history backfill for newly subscribed tags.
/// </summary>
public sealed class UpstreamSubscriptionAggregator : IDisposable
{
    private readonly ILogger _logger;
    private readonly ISubscriptionBroker _broker;
    private readonly BufferManager? _bufMgr;
    private readonly SourceManager _srcMgr;
    private readonly int _backfillMinutes;

    // source -> { tag -> refcount }  ("ALL" is tracked as a special key)
    private readonly Dictionary<string, Dictionary<string, int>> _refCounts = new(StringComparer.OrdinalIgnoreCase);
    // source -> set of tags currently subscribed upstream
    private readonly Dictionary<string, HashSet<string>> _upstreamTags = new(StringComparer.OrdinalIgnoreCase);
    // source -> BridgeWsClient
    private readonly Dictionary<string, BridgeWsClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    // source -> count of "ALL" subscribers
    private readonly Dictionary<string, int> _allSubCount = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();
    private Timer? _debounceTimer;
    private readonly HashSet<string> _dirtySources = new(StringComparer.OrdinalIgnoreCase);

    private const int DebounceMs = 80;

    public UpstreamSubscriptionAggregator(
        ISubscriptionBroker broker,
        SourceManager srcMgr,
        BufferManager? bufMgr,
        int backfillMinutes,
        ILogger logger)
    {
        _broker = broker;
        _srcMgr = srcMgr;
        _bufMgr = bufMgr;
        _backfillMinutes = backfillMinutes;
        _logger = logger;

        _broker.OnSubscriptionChanged += HandleSubscriptionChanged;
    }

    /// <summary>Register an upstream WS client for a source.</summary>
    public void RegisterClient(string sourceId, BridgeWsClient client)
    {
        lock (_lock)
        {
            _clients[sourceId] = client;
            _refCounts.TryAdd(sourceId, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            _upstreamTags.TryAdd(sourceId, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            _allSubCount.TryAdd(sourceId, 0);
        }

        client.OnConnected += OnClientReconnected;
    }

    /// <summary>Called when a WS client reconnects — re-push current subscription set.</summary>
    private void OnClientReconnected(BridgeWsClient client)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<string> tags;
                lock (_lock)
                {
                    if (!_upstreamTags.TryGetValue(client.SourceId, out var set) || set.Count == 0)
                        return;
                    tags = set.ToList();
                }

                _logger.LogInformation("Re-subscribing {Count} tags on reconnect for source {Source}", tags.Count, client.SourceId);
                await client.SubscribeTagsAsync(tags);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to re-subscribe on reconnect for source {Source}", client.SourceId);
            }
        });
    }

    private void HandleSubscriptionChanged(SubscriptionChangedEvent evt)
    {
        // Only aggregate subscriptions that target a specific source we have a client for
        if (evt.Source is null) return;

        lock (_lock)
        {
            if (!_clients.ContainsKey(evt.Source)) return;
            var refs = _refCounts.GetValueOrDefault(evt.Source);
            if (refs is null) return;

            // Process additions
            foreach (var tag in evt.AddedTags)
            {
                if (tag == "ALL")
                {
                    _allSubCount[evt.Source] = _allSubCount.GetValueOrDefault(evt.Source) + 1;
                }
                else
                {
                    refs[tag] = refs.GetValueOrDefault(tag) + 1;
                }
            }

            // Process removals
            if (evt.AllRemoved)
            {
                // When a connection disconnects, we get "all removed" — decrement everything
                foreach (var tag in evt.RemovedTags)
                {
                    if (tag == "ALL")
                    {
                        var c = _allSubCount.GetValueOrDefault(evt.Source);
                        if (c > 0) _allSubCount[evt.Source] = c - 1;
                    }
                    else
                    {
                        if (refs.TryGetValue(tag, out var cnt))
                        {
                            if (cnt <= 1) refs.Remove(tag);
                            else refs[tag] = cnt - 1;
                        }
                    }
                }
            }
            else
            {
                foreach (var tag in evt.RemovedTags)
                {
                    if (tag == "ALL")
                    {
                        var c = _allSubCount.GetValueOrDefault(evt.Source);
                        if (c > 0) _allSubCount[evt.Source] = c - 1;
                    }
                    else
                    {
                        if (refs.TryGetValue(tag, out var cnt))
                        {
                            if (cnt <= 1) refs.Remove(tag);
                            else refs[tag] = cnt - 1;
                        }
                    }
                }
            }

            _dirtySources.Add(evt.Source);
        }

        // Debounce: batch rapid changes
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ => FlushDirty(), null, DebounceMs, Timeout.Infinite);
    }

    private void FlushDirty()
    {
        List<(string source, List<string> toAdd, List<string> toRemove)> work;

        lock (_lock)
        {
            work = new List<(string, List<string>, List<string>)>();

            foreach (var sourceId in _dirtySources)
            {
                if (!_clients.ContainsKey(sourceId)) continue;
                var refs = _refCounts.GetValueOrDefault(sourceId);
                var upstream = _upstreamTags.GetValueOrDefault(sourceId);
                if (refs is null || upstream is null) continue;

                // Compute desired set
                HashSet<string> desired;
                if (_allSubCount.GetValueOrDefault(sourceId) > 0)
                {
                    desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ALL" };
                }
                else
                {
                    desired = new HashSet<string>(refs.Keys, StringComparer.OrdinalIgnoreCase);
                }

                // Compute delta
                var toAdd = new List<string>();
                var toRemove = new List<string>();

                foreach (var tag in desired)
                {
                    if (!upstream.Contains(tag))
                        toAdd.Add(tag);
                }
                foreach (var tag in upstream)
                {
                    if (!desired.Contains(tag))
                        toRemove.Add(tag);
                }

                // Update upstream set
                upstream.Clear();
                foreach (var tag in desired)
                    upstream.Add(tag);

                if (toAdd.Count > 0 || toRemove.Count > 0)
                    work.Add((sourceId, toAdd, toRemove));
            }

            _dirtySources.Clear();
        }

        foreach (var (sourceId, toAdd, toRemove) in work)
        {
            _ = ApplyDeltaAsync(sourceId, toAdd, toRemove);
        }
    }

    private async Task ApplyDeltaAsync(string sourceId, List<string> toAdd, List<string> toRemove)
    {
        BridgeWsClient? client;
        lock (_lock) { _clients.TryGetValue(sourceId, out client); }
        if (client is null || !client.IsConnected) return;

        try
        {
            if (toRemove.Count > 0)
            {
                _logger.LogInformation("Upstream unsubscribe {Source}: -{Tags}", sourceId, string.Join(",", toRemove));
                await client.UnsubscribeTagsAsync(toRemove);
            }

            if (toAdd.Count > 0)
            {
                _logger.LogInformation("Upstream subscribe {Source}: +{Tags}", sourceId, string.Join(",", toAdd));
                await client.SubscribeTagsAsync(toAdd);

                // Backfill newly subscribed tags (skip if "ALL" — too much data)
                if (_backfillMinutes > 0 && !toAdd.Contains("ALL"))
                {
                    _ = BackfillAsync(sourceId, client, toAdd.ToArray());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply subscription delta for source {Source}", sourceId);
        }
    }

    private async Task BackfillAsync(string sourceId, BridgeWsClient client, string[] tags)
    {
        try
        {
            var to = DateTimeOffset.UtcNow;
            var from = to.AddMinutes(-_backfillMinutes);

            _logger.LogDebug("Backfilling {Count} tags for source {Source} ({Minutes}min)", tags.Length, sourceId, _backfillMinutes);
            var response = await client.QueryTelemetryAsync(tags, from, to);

            // Parse response and feed into buffer
            if (response.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
            {
                int count = 0;
                foreach (var group in dataProp.EnumerateArray())
                {
                    var tag = group.TryGetProperty("tag", out var tagProp) ? tagProp.GetString() : null;
                    if (tag is null) continue;

                    if (!group.TryGetProperty("values", out var values)) continue;
                    foreach (var val in values.EnumerateArray())
                    {
                        object? value = null;
                        if (val.TryGetProperty("v", out var vProp))
                        {
                            value = vProp.ValueKind switch
                            {
                                JsonValueKind.Number => vProp.GetDouble(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                JsonValueKind.String => vProp.GetString(),
                                _ => vProp.ToString()
                            };
                        }

                        var ts = val.TryGetProperty("ts", out var tsProp)
                            ? DateTimeOffset.Parse(tsProp.GetString()!)
                            : DateTimeOffset.UtcNow;
                        var msgId = val.TryGetProperty("msgId", out var mProp)
                            ? mProp.GetUInt32()
                            : 0u;

                        var tv = new TagValue
                        {
                            Source = sourceId,
                            Tag = tag,
                            Kind = DataKind.Telemetry,
                            Value = value,
                            Timestamp = ts,
                            MsgId = msgId
                        };

                        _bufMgr?.Add(tv);
                        count++;
                    }
                }

                if (count > 0)
                    _logger.LogInformation("Backfilled {Count} values for source {Source}", count, sourceId);
            }
        }
        catch (OperationCanceledException) { /* timeout, not critical */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backfill failed for source {Source}", sourceId);
        }
    }

    public void Dispose()
    {
        _broker.OnSubscriptionChanged -= HandleSubscriptionChanged;
        _debounceTimer?.Dispose();

        lock (_lock)
        {
            foreach (var client in _clients.Values)
                client.OnConnected -= OnClientReconnected;
        }
    }
}
