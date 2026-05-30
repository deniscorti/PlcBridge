using System.Collections.Concurrent;

namespace Bridge.Host.WebSockets;

/// <summary>
/// Accumulates values for compact-mode connections, then flushes them as
/// positional-array batches. One batch per (connection, source, tick window).
///
/// Push format:
///   { "type":"d", "l":layoutId, "ts":"...", "v":[23.5, null, 18.2, ...] }
///
/// v[i] corresponds to tags[i] in the layout. null = no update this tick.
/// </summary>
public sealed class CompactBatchAccumulator : IDisposable
{
    private readonly CompactLayoutManager _layoutMgr;
    private readonly WsConnectionManager _connMgr;
    private readonly int _flushIntervalMs;
    private readonly Timer _flushTimer;

    // Key: "connId|source" → PendingBatch
    private readonly ConcurrentDictionary<string, PendingBatch> _pending = new();

    public CompactBatchAccumulator(CompactLayoutManager layoutMgr, WsConnectionManager connMgr, int flushIntervalMs = 50)
    {
        _layoutMgr = layoutMgr;
        _connMgr = connMgr;
        _flushIntervalMs = flushIntervalMs;

        _flushTimer = new Timer(_ => Flush(), null, _flushIntervalMs, _flushIntervalMs);
    }

    /// <summary>
    /// Accumulate a value for a compact-mode connection.
    /// Returns true if the value was accumulated (connection has compact layout for this source).
    /// Returns false if the connection doesn't use compact mode — caller should send verbose.
    /// </summary>
    public bool TryAccumulate(string connectionId, string source, string tag, object? value, DateTimeOffset ts, uint msgId)
    {
        var pos = _layoutMgr.GetTagIndex(connectionId, source, tag);
        if (pos is null) return false;

        var key = string.Concat(connectionId, "|", source);
        var batch = _pending.GetOrAdd(key, _ => new PendingBatch(connectionId, source, pos.Value.layoutId));

        // If layoutId changed (layout was updated while batch was pending), reset
        if (batch.LayoutId != pos.Value.layoutId)
        {
            batch = new PendingBatch(connectionId, source, pos.Value.layoutId);
            _pending[key] = batch;
        }

        batch.Set(pos.Value.index, value, ts, msgId);
        return true;
    }

    private void Flush()
    {
        // Snapshot and clear all pending batches
        var keys = _pending.Keys.ToArray();
        foreach (var key in keys)
        {
            if (!_pending.TryRemove(key, out var batch)) continue;
            if (batch.IsEmpty) continue;

            _ = SendBatchAsync(batch);
        }
    }

    private async Task SendBatchAsync(PendingBatch batch)
    {
        try
        {
            var layout = _layoutMgr.GetLayout(batch.ConnectionId, batch.Source);
            if (layout is null) return;

            // Build positional value array (same size as layout.Tags)
            var values = new object?[layout.Tags.Length];
            int count = 0;
            foreach (var (index, val) in batch.Values)
            {
                if (index < values.Length)
                {
                    values[index] = val;
                    count++;
                }
            }

            if (count == 0) return;

            await _connMgr.SendAsync(batch.ConnectionId, new
            {
                type = "d",
                l = batch.LayoutId,
                ts = batch.LatestTs,
                m = batch.LatestMsgId,
                v = values
            });
        }
        catch
        {
            // Connection may have closed
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        // Flush remaining
        Flush();
    }

    private sealed class PendingBatch
    {
        public string ConnectionId { get; }
        public string Source { get; }
        public int LayoutId { get; }
        public DateTimeOffset LatestTs { get; private set; }
        public uint LatestMsgId { get; private set; }
        public bool IsEmpty => Values.Count == 0;

        // index → value
        public Dictionary<int, object?> Values { get; } = new();

        public PendingBatch(string connectionId, string source, int layoutId)
        {
            ConnectionId = connectionId;
            Source = source;
            LayoutId = layoutId;
        }

        public void Set(int index, object? value, DateTimeOffset ts, uint msgId)
        {
            Values[index] = value;
            if (ts > LatestTs) LatestTs = ts;
            if (msgId > LatestMsgId) LatestMsgId = msgId;
        }
    }
}
