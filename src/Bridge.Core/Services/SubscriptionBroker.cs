using System.Collections.Concurrent;
using Bridge.Core.Abstractions;
using Bridge.Core.Model;

namespace Bridge.Core.Services;

public sealed class SubscriptionBroker : ISubscriptionBroker
{
    // connectionId -> set of subscription keys ("source:tag" or "source:*" or "*:kind")
    private readonly ConcurrentDictionary<string, HashSet<string>> _subscriptions = new();

    public event Action<SubscriptionChangedEvent>? OnSubscriptionChanged;

    public void Subscribe(string connectionId, string? source, IReadOnlyList<string> tags, IReadOnlyList<DataKind>? kinds = null)
    {
        var subs = _subscriptions.GetOrAdd(connectionId, _ => new HashSet<string>());
        var added = new List<string>();

        lock (subs)
        {
            if (kinds is { Count: > 0 })
            {
                foreach (var kind in kinds)
                {
                    var key = source is null ? $"*:kind:{kind}" : $"{source}:kind:{kind}";
                    subs.Add(key);
                }
                return;
            }

            foreach (var tag in tags)
            {
                string key;
                if (tag == "ALL")
                    key = source is null ? "*:*" : $"{source}:*";
                else
                    key = source is null ? $"*:{tag}" : $"{source}:{tag}";

                if (subs.Add(key))
                    added.Add(tag);
            }
        }

        if (added.Count > 0)
            OnSubscriptionChanged?.Invoke(new SubscriptionChangedEvent(connectionId, source, added, [], false));
    }

    public void Unsubscribe(string connectionId, string? source, IReadOnlyList<string>? tags = null)
    {
        if (!_subscriptions.TryGetValue(connectionId, out var subs)) return;
        var removed = new List<string>();

        lock (subs)
        {
            if (tags is null or { Count: 0 })
            {
                // Unsubscribe all for this source
                if (source is null)
                {
                    removed.AddRange(ExtractTagNames(subs, null));
                    subs.Clear();
                }
                else
                {
                    removed.AddRange(ExtractTagNames(subs, source));
                    subs.RemoveWhere(k => k.StartsWith($"{source}:", StringComparison.OrdinalIgnoreCase));
                }

                if (removed.Count > 0)
                    OnSubscriptionChanged?.Invoke(new SubscriptionChangedEvent(connectionId, source, [], removed, true));
                return;
            }

            foreach (var tag in tags)
            {
                if (tag == "ALL")
                {
                    if (source is null)
                    {
                        removed.AddRange(ExtractTagNames(subs, null));
                        subs.Clear();
                    }
                    else
                    {
                        removed.AddRange(ExtractTagNames(subs, source));
                        subs.RemoveWhere(k => k.StartsWith($"{source}:", StringComparison.OrdinalIgnoreCase));
                    }
                }
                else
                {
                    var key = source is null ? $"*:{tag}" : $"{source}:{tag}";
                    if (subs.Remove(key))
                        removed.Add(tag);
                }
            }
        }

        if (removed.Count > 0)
            OnSubscriptionChanged?.Invoke(new SubscriptionChangedEvent(connectionId, source, [], removed, false));
    }

    public void UnsubscribeAll(string connectionId)
    {
        if (!_subscriptions.TryRemove(connectionId, out var subs)) return;

        // Collect per-source removals
        var bySource = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        lock (subs)
        {
            foreach (var key in subs)
            {
                var parts = key.Split(':', 2);
                var src = parts[0];
                var tag = parts.Length > 1 ? parts[1] : "*";
                if (!bySource.TryGetValue(src, out var list))
                {
                    list = [];
                    bySource[src] = list;
                }
                list.Add(tag == "*" ? "ALL" : tag);
            }
        }

        foreach (var (src, tags) in bySource)
            OnSubscriptionChanged?.Invoke(new SubscriptionChangedEvent(connectionId, src == "*" ? null : src, [], tags, true));
    }

    public IReadOnlyList<string> GetSubscribers(TagValue value)
    {
        var result = new List<string>();
        foreach (var (connId, subs) in _subscriptions)
        {
            lock (subs)
            {
                if (subs.Contains($"{value.Source}:{value.Tag}") ||    // exact source:tag
                    subs.Contains($"{value.Source}:*") ||               // all tags in source
                    subs.Contains($"*:{value.Tag}") ||                 // tag across all sources
                    subs.Contains("*:*") ||                            // everything
                    subs.Contains($"{value.Source}:kind:{value.Kind}") || // kind in source
                    subs.Contains($"*:kind:{value.Kind}"))             // kind across all sources
                {
                    result.Add(connId);
                }
            }
        }
        return result;
    }

    /// <summary>Extract readable tag names from subscription keys for a given source.</summary>
    private static List<string> ExtractTagNames(HashSet<string> subs, string? source)
    {
        var tags = new List<string>();
        var prefix = source is null ? "*:" : $"{source}:";
        foreach (var key in subs)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var tag = key[prefix.Length..];
            if (tag.StartsWith("kind:", StringComparison.Ordinal)) continue;
            tags.Add(tag == "*" ? "ALL" : tag);
        }
        return tags;
    }
}
