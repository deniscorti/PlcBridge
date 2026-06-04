using Bridge.Core.Model;

namespace Bridge.Core.Abstractions;

/// <summary>
/// Manages WebSocket subscriptions and dispatches push messages to subscribers.
/// </summary>
public interface ISubscriptionBroker
{
    /// <summary>Register a subscriber for specific tags in a source.</summary>
    void Subscribe(string connectionId, string? source, IReadOnlyList<string> tags, IReadOnlyList<DataKind>? kinds = null);

    /// <summary>Remove subscription for specific tags.</summary>
    void Unsubscribe(string connectionId, string? source, IReadOnlyList<string>? tags = null);

    /// <summary>Remove all subscriptions for a connection.</summary>
    void UnsubscribeAll(string connectionId);

    /// <summary>Publish a value to all matching subscribers. Returns the set of connectionIds that should receive it.</summary>
    IReadOnlyList<string> GetSubscribers(TagValue value);

    /// <summary>Fired when a subscription changes. Used by UpstreamSubscriptionAggregator.</summary>
    event Action<SubscriptionChangedEvent>? OnSubscriptionChanged;
}

/// <summary>
/// Describes a change in subscriptions for a connection.
/// </summary>
public sealed record SubscriptionChangedEvent(
    string ConnectionId,
    string? Source,
    IReadOnlyList<string> AddedTags,
    IReadOnlyList<string> RemovedTags,
    bool AllRemoved);
