using System.Collections.Concurrent;
using Bridge.Core.Buffer;
using Bridge.Core.Model;

namespace Bridge.Core.Services;

/// <summary>
/// Manages ChunkedRingBuffers for all DataSources.
/// DataService and DataServer modes use this to buffer incoming data.
/// </summary>
public sealed class BufferManager
{
    private readonly ConcurrentDictionary<string, ChunkedRingBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private int _chunkDurationMin;
    private int _inMemoryMinutes;

    /// <summary>Fired when any chunk is sealed (for Parquet flush, chunk transfer).</summary>
    public event Action<Chunk>? OnChunkSealed;

    public BufferManager(int chunkDurationMin, int inMemoryMinutes)
    {
        _chunkDurationMin = chunkDurationMin;
        _inMemoryMinutes = inMemoryMinutes;
    }

    public ChunkedRingBuffer GetOrCreateBuffer(string sourceId)
    {
        return _buffers.GetOrAdd(sourceId, id =>
        {
            var buf = new ChunkedRingBuffer(id, _chunkDurationMin, _inMemoryMinutes);
            buf.OnChunkSealed += chunk => OnChunkSealed?.Invoke(chunk);
            return buf;
        });
    }

    public ChunkedRingBuffer? GetBuffer(string sourceId) => _buffers.GetValueOrDefault(sourceId);

    public IReadOnlyCollection<string> GetBufferedSources() => _buffers.Keys.ToList();

    /// <summary>Add a value to the appropriate buffer.</summary>
    public void Add(TagValue value)
    {
        var buffer = GetOrCreateBuffer(value.Source);
        buffer.Add(value);
    }

    /// <summary>Change chunk duration for all buffers. Takes effect at next seal.</summary>
    public void SetChunkDuration(int minutes)
    {
        _chunkDurationMin = minutes;
        foreach (var buf in _buffers.Values)
            buf.SetChunkDuration(minutes);
    }

    public int ChunkDurationMin => _chunkDurationMin;
    public int InMemoryMinutes => _inMemoryMinutes;
}
