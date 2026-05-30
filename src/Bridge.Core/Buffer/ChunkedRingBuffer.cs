using Bridge.Core.Model;

namespace Bridge.Core.Buffer;

/// <summary>
/// Circular buffer of time-based chunks for a single DataSource.
/// Handles: create/seal chunks, eviction, query by time range.
/// Thread-safe.
/// </summary>
public sealed class ChunkedRingBuffer
{
    private readonly object _lock = new();
    private readonly LinkedList<Chunk> _chunks = new();
    private Chunk? _active;
    private readonly string _source;
    private TimeSpan _chunkDuration;
    private readonly TimeSpan _maxRetention;

    /// <summary>Fired when a chunk is sealed. Used to trigger Parquet flush / chunk transfer notification.</summary>
    public event Action<Chunk>? OnChunkSealed;

    public ChunkedRingBuffer(string source, int chunkDurationMin, int inMemoryMinutes)
    {
        _source = source;
        _chunkDuration = TimeSpan.FromMinutes(chunkDurationMin);
        _maxRetention = TimeSpan.FromMinutes(inMemoryMinutes);
    }

    public int ChunkCount { get { lock (_lock) return _chunks.Count; } }
    public TimeSpan ChunkDuration => _chunkDuration;

    /// <summary>Change chunk duration. Takes effect at next seal.</summary>
    public void SetChunkDuration(int minutes)
    {
        _chunkDuration = TimeSpan.FromMinutes(minutes);
    }

    /// <summary>Add a value to the buffer. Creates/seals chunks as needed.</summary>
    public void Add(TagValue value)
    {
        Chunk? sealed_ = null;

        lock (_lock)
        {
            if (_active is null || !_active.Accepts(value.Timestamp))
            {
                // Seal current
                if (_active is not null && _active.State == ChunkState.Open)
                {
                    _active.Seal();
                    sealed_ = _active;
                }

                // Create new chunk aligned to the value's timestamp
                var fromTs = AlignTimestamp(value.Timestamp);
                _active = new Chunk(_source, fromTs, _chunkDuration);
                _chunks.AddLast(_active);

                // Evict old chunks
                Evict();
            }

            _active.TryAdd(value);
        }

        if (sealed_ is not null)
            OnChunkSealed?.Invoke(sealed_);
    }

    /// <summary>Get all sealed chunks (for Parquet flush, chunk transfer, etc.).</summary>
    public IReadOnlyList<Chunk> GetSealedChunks()
    {
        lock (_lock)
        {
            return _chunks.Where(c => c.State == ChunkState.Sealed).ToList();
        }
    }

    /// <summary>Get the active (open) chunk.</summary>
    public Chunk? GetActiveChunk()
    {
        lock (_lock) return _active;
    }

    /// <summary>Replace a chunk (e.g., Live→Full quality upgrade).</summary>
    public void ReplaceChunk(Chunk newChunk)
    {
        lock (_lock)
        {
            var node = _chunks.First;
            while (node is not null)
            {
                if (node.Value.ChunkId == newChunk.ChunkId && node.Value != _active)
                {
                    // Handle boundary: move any values from old chunk that are after new chunk's ToTs
                    var overflow = node.Value.ExtractAfter(newChunk.ToTs);
                    node.Value = newChunk;

                    // If there's overflow, add to next chunk
                    if (overflow.Count > 0 && node.Next is not null)
                    {
                        foreach (var v in overflow)
                            node.Next.Value.TryAdd(v);
                    }
                    return;
                }
                node = node.Next;
            }
            // Not found — just insert in order
            InsertInOrder(newChunk);
        }
    }

    /// <summary>Insert a loaded/transferred chunk in chronological order.</summary>
    public void InsertChunk(Chunk chunk)
    {
        lock (_lock)
        {
            InsertInOrder(chunk);
            Evict();
        }
    }

    public IReadOnlyList<TagValue> QueryTelemetry(string[]? tags, DateTimeOffset from, DateTimeOffset to, int limit = 10000)
    {
        return QueryInternal(tags, from, to, limit, (c, t, f, tt) => c.GetTelemetry(t, f, tt));
    }

    public IReadOnlyList<TagValue> QueryEvents(string[]? tags, DateTimeOffset from, DateTimeOffset to, int limit = 10000)
    {
        return QueryInternal(tags, from, to, limit, (c, t, f, tt) => c.GetEvents(t, f, tt));
    }

    public IReadOnlyList<TagValue> QueryAlarms(string[]? tags, DateTimeOffset from, DateTimeOffset to, int limit = 10000)
    {
        return QueryInternal(tags, from, to, limit, (c, t, f, tt) => c.GetAlarms(t, f, tt));
    }

    /// <summary>Get the overall quality of data in a time range.</summary>
    public string GetQuality(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            var qualities = new HashSet<ChunkQuality>();
            foreach (var chunk in _chunks)
            {
                if (chunk.ToTs <= from || chunk.FromTs >= to) continue;
                qualities.Add(chunk.Quality);
            }
            if (qualities.Count == 0) return "none";
            if (qualities.Count == 1) return qualities.First().ToString().ToLowerInvariant();
            return "mixed";
        }
    }

    /// <summary>Get the oldest available timestamp in the buffer.</summary>
    public DateTimeOffset? OldestTimestamp
    {
        get { lock (_lock) return _chunks.First?.Value.FromTs; }
    }

    /// <summary>Get the newest available timestamp in the buffer.</summary>
    public DateTimeOffset? NewestTimestamp
    {
        get { lock (_lock) return _active?.ToTs ?? _chunks.Last?.Value.ToTs; }
    }

    public int TotalRecords
    {
        get { lock (_lock) return _chunks.Sum(c => c.RecordCount); }
    }

    public (uint oldest, uint newest) MsgIdRange
    {
        get
        {
            lock (_lock)
            {
                if (_chunks.Count == 0) return (0, 0);
                return (_chunks.First!.Value.FirstMsgId, _chunks.Last!.Value.LastMsgId);
            }
        }
    }

    /// <summary>Get metadata for all chunks (for status/info endpoints).</summary>
    public IReadOnlyList<ChunkInfo> GetChunkInfos()
    {
        lock (_lock)
        {
            return _chunks.Select(c => new ChunkInfo(
                c.ChunkId, c.Source, c.FromTs, c.ToTs,
                c.FirstMsgId, c.LastMsgId, c.RecordCount,
                c.State, c.Quality
            )).ToList();
        }
    }

    private List<TagValue> QueryInternal(
        string[]? tags, DateTimeOffset from, DateTimeOffset to, int limit,
        Func<Chunk, string[]?, DateTimeOffset, DateTimeOffset, IReadOnlyList<TagValue>> getter)
    {
        var result = new List<TagValue>();
        lock (_lock)
        {
            foreach (var chunk in _chunks)
            {
                if (chunk.ToTs <= from || chunk.FromTs >= to) continue;
                var values = getter(chunk, tags, from, to);
                result.AddRange(values);
                if (result.Count >= limit) break;
            }
        }
        if (result.Count > limit) result.RemoveRange(limit, result.Count - limit);
        return result;
    }

    private void Evict()
    {
        var cutoff = DateTimeOffset.UtcNow - _maxRetention;
        while (_chunks.Count > 1 && _chunks.First!.Value.ToTs < cutoff && _chunks.First.Value != _active)
        {
            _chunks.RemoveFirst();
        }
    }

    private void InsertInOrder(Chunk chunk)
    {
        var node = _chunks.Last;
        while (node is not null && node.Value.FromTs > chunk.FromTs)
            node = node.Previous;

        if (node is null)
            _chunks.AddFirst(chunk);
        else
            _chunks.AddAfter(node, chunk);
    }

    private DateTimeOffset AlignTimestamp(DateTimeOffset ts)
    {
        var minutes = (int)_chunkDuration.TotalMinutes;
        if (minutes <= 0) minutes = 1;
        var aligned = new DateTimeOffset(
            ts.Year, ts.Month, ts.Day,
            ts.Hour, ts.Minute - (ts.Minute % minutes), 0, ts.Offset);
        return aligned;
    }
}

public sealed record ChunkInfo(
    string ChunkId, string Source,
    DateTimeOffset FromTs, DateTimeOffset ToTs,
    uint FirstMsgId, uint LastMsgId,
    int RecordCount, ChunkState State, ChunkQuality Quality);
