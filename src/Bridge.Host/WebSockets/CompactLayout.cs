namespace Bridge.Host.WebSockets;

/// <summary>
/// Manages compact push layouts for WS connections that opt-in to compact mode.
/// Each layout maps tag positions to tag names, versioned by layoutId.
/// When subscriptions change, a new layout is generated and sent to the client.
/// </summary>
public sealed class CompactLayoutManager
{
    // connectionId -> LayoutState
    private readonly Dictionary<string, LayoutState> _layouts = new();
    private readonly object _lock = new();

    /// <summary>Enable compact mode for a connection. Returns the initial layout.</summary>
    public CompactLayout EnableCompact(string connectionId, string source, IReadOnlyList<string> tags)
    {
        lock (_lock)
        {
            var state = GetOrCreateState(connectionId);
            var layout = state.AddOrUpdateSource(source, tags);
            return layout;
        }
    }

    /// <summary>Update layout when tags are added to an existing subscription.</summary>
    public CompactLayout? AddTags(string connectionId, string source, IReadOnlyList<string> tags)
    {
        lock (_lock)
        {
            if (!_layouts.TryGetValue(connectionId, out var state)) return null;
            if (!state.IsCompact) return null;

            var existing = state.GetTags(source);
            var merged = new List<string>(existing);
            foreach (var tag in tags)
            {
                if (!merged.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    merged.Add(tag);
            }
            return state.AddOrUpdateSource(source, merged);
        }
    }

    /// <summary>Update layout when tags are removed.</summary>
    public CompactLayout? RemoveTags(string connectionId, string source, IReadOnlyList<string>? tags)
    {
        lock (_lock)
        {
            if (!_layouts.TryGetValue(connectionId, out var state)) return null;
            if (!state.IsCompact) return null;

            if (tags is null or { Count: 0 })
            {
                // Remove entire source
                state.RemoveSource(source);
                return new CompactLayout(source, state.NextLayoutId(), []);
            }

            var existing = state.GetTags(source);
            var remaining = existing.Where(t => !tags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            return state.AddOrUpdateSource(source, remaining);
        }
    }

    /// <summary>Check if a connection uses compact mode.</summary>
    public bool IsCompact(string connectionId)
    {
        lock (_lock)
        {
            return _layouts.TryGetValue(connectionId, out var state) && state.IsCompact;
        }
    }

    /// <summary>Get current layout for a source on a connection. Returns null if not compact or no layout.</summary>
    public CompactLayout? GetLayout(string connectionId, string source)
    {
        lock (_lock)
        {
            if (!_layouts.TryGetValue(connectionId, out var state)) return null;
            return state.GetLayout(source);
        }
    }

    /// <summary>Try to get the tag index for a value within a connection's layout.</summary>
    public (int layoutId, int index)? GetTagIndex(string connectionId, string source, string tag)
    {
        lock (_lock)
        {
            if (!_layouts.TryGetValue(connectionId, out var state)) return null;
            return state.GetTagIndex(source, tag);
        }
    }

    /// <summary>Remove all layouts for a connection.</summary>
    public void RemoveConnection(string connectionId)
    {
        lock (_lock) { _layouts.Remove(connectionId); }
    }

    private LayoutState GetOrCreateState(string connectionId)
    {
        if (!_layouts.TryGetValue(connectionId, out var state))
        {
            state = new LayoutState();
            _layouts[connectionId] = state;
        }
        return state;
    }

    private sealed class LayoutState
    {
        public bool IsCompact => _sourceLayouts.Count > 0;
        private int _layoutVersion;
        // source -> CompactLayout
        private readonly Dictionary<string, CompactLayout> _sourceLayouts = new(StringComparer.OrdinalIgnoreCase);

        public int NextLayoutId() => Interlocked.Increment(ref _layoutVersion);

        public CompactLayout AddOrUpdateSource(string source, IReadOnlyList<string> tags)
        {
            var layout = new CompactLayout(source, NextLayoutId(), tags.ToArray());
            _sourceLayouts[source] = layout;
            return layout;
        }

        public void RemoveSource(string source) => _sourceLayouts.Remove(source);

        public string[] GetTags(string source)
        {
            return _sourceLayouts.TryGetValue(source, out var layout) ? layout.Tags : Array.Empty<string>();
        }

        public CompactLayout? GetLayout(string source)
        {
            return _sourceLayouts.GetValueOrDefault(source);
        }

        public (int layoutId, int index)? GetTagIndex(string source, string tag)
        {
            if (!_sourceLayouts.TryGetValue(source, out var layout)) return null;
            var idx = Array.FindIndex(layout.Tags, t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 ? (layout.LayoutId, idx) : null;
        }
    }
}

/// <summary>
/// A layout defines the ordered tag mapping for compact push messages.
/// </summary>
public sealed record CompactLayout(string Source, int LayoutId, string[] Tags);
