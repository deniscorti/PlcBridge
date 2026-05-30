using System.Text.Json;
using Bridge.Core.Buffer;
using Bridge.Core.Services;
using Bridge.InterBridge.WsClient;
using Microsoft.Extensions.Logging;

namespace Bridge.InterBridge.ChunkTransfer;

/// <summary>
/// Manages background chunk transfer from DataService to DataServer.
/// Prioritizes newest chunks first so the most recent data is available quickly.
/// </summary>
public sealed class ChunkTransferService
{
    private readonly BufferManager _bufferManager;
    private readonly ILogger _logger;
    private readonly object _queueLock = new();
    private readonly List<ChunkTransferRequest> _pending = [];
    private bool _processing;
    private int _completedTransfers;
    private long _totalTransferMs;

    public int PendingCount { get { lock (_queueLock) return _pending.Count; } }
    public int CompletedCount => _completedTransfers;
    public long AvgTransferMs => _completedTransfers > 0 ? _totalTransferMs / _completedTransfers : 0;

    public ChunkTransferService(BufferManager bufferManager, ILogger<ChunkTransferService> logger)
    {
        _bufferManager = bufferManager;
        _logger = logger;
    }

    /// <summary>
    /// DataServer: handle chunkReady notification from upstream.
    /// Queues a transfer request prioritized by timestamp (newest first).
    /// </summary>
    public void OnChunkReady(string sourceId, BridgeWsClient client, JsonElement chunkInfo)
    {
        var chunkId = chunkInfo.GetProperty("chunkId").GetString()!;
        var fromTs = DateTimeOffset.Parse(chunkInfo.GetProperty("fromTs").GetString()!);
        var toTs = DateTimeOffset.Parse(chunkInfo.GetProperty("toTs").GetString()!);
        var records = chunkInfo.TryGetProperty("records", out var r) ? r.GetInt32() : 0;

        _logger.LogInformation("Chunk ready: {ChunkId} for source {Source} ({Records} records, {From} → {To})",
            chunkId, sourceId, records, fromTs, toTs);

        var req = new ChunkTransferRequest(sourceId, chunkId, fromTs, toTs, client);

        lock (_queueLock)
        {
            // Insert sorted: newest (highest ToTs) first
            var idx = _pending.FindIndex(r => r.ToTs < req.ToTs);
            if (idx < 0)
                _pending.Add(req); // oldest so far, add at end
            else
                _pending.Insert(idx, req);
        }

        _ = ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        // Only one processor at a time
        lock (_queueLock)
        {
            if (_processing) return;
            _processing = true;
        }

        try
        {
            while (true)
            {
                ChunkTransferRequest? req;
                lock (_queueLock)
                {
                    if (_pending.Count == 0) break;
                    req = _pending[0];
                    _pending.RemoveAt(0);
                }

                await TransferChunkAsync(req);
            }
        }
        finally
        {
            lock (_queueLock) _processing = false;
        }

        // Check if new items were added while finishing
        bool hasMore;
        lock (_queueLock) hasMore = _pending.Count > 0;
        if (hasMore) _ = ProcessQueueAsync();
    }

    private async Task TransferChunkAsync(ChunkTransferRequest req)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _logger.LogInformation("Requesting chunk {ChunkId} from source {Source} (priority: {ToTs})",
                req.ChunkId, req.SourceId, req.ToTs);

            var result = await req.Client.RequestChunkAsync(req.ChunkId);

            // Create a Full quality chunk to replace the Live one in the buffer
            var chunk = new Chunk(req.SourceId, req.FromTs, req.ToTs, ChunkQuality.Full);
            chunk.Seal();

            _bufferManager.GetOrCreateBuffer(req.SourceId).ReplaceChunk(chunk);

            sw.Stop();
            Interlocked.Increment(ref _completedTransfers);
            Interlocked.Add(ref _totalTransferMs, sw.ElapsedMilliseconds);

            _logger.LogInformation("Chunk {ChunkId} transferred in {Ms}ms", req.ChunkId, sw.ElapsedMilliseconds);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Chunk transfer timeout for {ChunkId}", req.ChunkId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chunk transfer failed for {ChunkId}", req.ChunkId);
        }
    }

    private sealed record ChunkTransferRequest(
        string SourceId, string ChunkId,
        DateTimeOffset FromTs, DateTimeOffset ToTs,
        BridgeWsClient Client);
}
