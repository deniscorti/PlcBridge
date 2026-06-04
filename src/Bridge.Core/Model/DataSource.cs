using System.Collections.Concurrent;

namespace Bridge.Core.Model;

/// <summary>
/// Logical grouping of tags. Each DataSource has a unique Id and contains
/// tags with unique names within the source.
/// </summary>
public sealed class DataSource
{
    public required string Id { get; init; }

    private readonly ConcurrentDictionary<string, Tag> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TagValue> _lastValues = new(StringComparer.OrdinalIgnoreCase);
    private uint _nextMsgId;

    public IReadOnlyDictionary<string, Tag> Tags => _tags;

    /// <summary>Fired when a new tag is registered in this source. Args: (sourceId, tag).</summary>
    public event Action<string, Tag>? OnTagRegistered;

    public void RegisterTag(Tag tag)
    {
        if (!_tags.TryAdd(tag.Name, tag))
            throw new InvalidOperationException($"Tag '{tag.Name}' already registered in source '{Id}'.");
        tag.TagId = Crc32.Compute(tag.Name);
        OnTagRegistered?.Invoke(Id, tag);
    }

    public bool TryRegisterTag(Tag tag)
    {
        if (!_tags.TryAdd(tag.Name, tag))
            return false;
        tag.TagId = Crc32.Compute(tag.Name);
        OnTagRegistered?.Invoke(Id, tag);
        return true;
    }

    public Tag? GetTag(string name) => _tags.GetValueOrDefault(name);

    /// <summary>
    /// Assigns the next incremental MsgId for this DataSource.
    /// </summary>
    public uint NextMsgId() => Interlocked.Increment(ref _nextMsgId);

    /// <summary>
    /// Stores or updates the last known value for a tag.
    /// </summary>
    public void SetLastValue(TagValue value)
    {
        _lastValues[value.Tag] = value;
    }

    public TagValue? GetLastValue(string tag) => _lastValues.GetValueOrDefault(tag);

    public IEnumerable<TagValue> GetAllLastValues() => _lastValues.Values;
}
