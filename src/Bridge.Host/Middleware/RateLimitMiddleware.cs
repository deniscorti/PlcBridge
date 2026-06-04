using System.Collections.Concurrent;

namespace Bridge.Host.Middleware;

/// <summary>
/// Per-connection rate limiter for WebSocket queries.
/// Tracks queries per minute per connection.
/// </summary>
public sealed class RateLimiter
{
    private readonly int _maxQueriesPerMinute;
    private readonly int _maxSubscriptionsPerClient;
    private readonly int _maxMessageQueueSize;
    private readonly ConcurrentDictionary<string, ConnectionRateInfo> _connections = new();

    public RateLimiter(int maxQueriesPerMinute, int maxSubscriptionsPerClient, int maxMessageQueueSize)
    {
        _maxQueriesPerMinute = maxQueriesPerMinute;
        _maxSubscriptionsPerClient = maxSubscriptionsPerClient;
        _maxMessageQueueSize = maxMessageQueueSize;
    }

    public bool TryConsumeQuery(string connectionId)
    {
        var info = _connections.GetOrAdd(connectionId, _ => new ConnectionRateInfo());
        return info.TryConsumeQuery(_maxQueriesPerMinute);
    }

    public bool CanSubscribe(string connectionId, int additionalCount = 1)
    {
        var info = _connections.GetOrAdd(connectionId, _ => new ConnectionRateInfo());
        return info.SubscriptionCount + additionalCount <= _maxSubscriptionsPerClient;
    }

    public void AddSubscription(string connectionId, int count = 1)
    {
        var info = _connections.GetOrAdd(connectionId, _ => new ConnectionRateInfo());
        Interlocked.Add(ref info.SubscriptionCount, count);
    }

    public void RemoveSubscription(string connectionId, int count = 1)
    {
        if (_connections.TryGetValue(connectionId, out var info))
            Interlocked.Add(ref info.SubscriptionCount, -count);
    }

    public void RemoveConnection(string connectionId) => _connections.TryRemove(connectionId, out _);

    public int MaxQueriesPerMinute => _maxQueriesPerMinute;
    public int MaxSubscriptionsPerClient => _maxSubscriptionsPerClient;
}

internal sealed class ConnectionRateInfo
{
    private readonly Queue<DateTimeOffset> _queryTimes = new();
    private readonly object _lock = new();
    public int SubscriptionCount;

    public bool TryConsumeQuery(int maxPerMinute)
    {
        if (maxPerMinute <= 0) return true;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddMinutes(-1);
            while (_queryTimes.Count > 0 && _queryTimes.Peek() < cutoff)
                _queryTimes.Dequeue();

            if (_queryTimes.Count >= maxPerMinute) return false;
            _queryTimes.Enqueue(now);
            return true;
        }
    }
}
