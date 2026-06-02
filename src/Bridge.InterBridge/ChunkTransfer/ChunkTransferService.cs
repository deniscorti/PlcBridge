using System.Text.Json;
using Bridge.Core.Buffer;
using Bridge.Core.Model;
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

    /// <summary>Fired when a full-quality chunk has been transferred and inserted into the buffer.</summary>
    public event Action<Chunk>? OnChunkTransferred;

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

            var chunk = new Chunk(req.SourceId, req.FromTs, req.ToTs, ChunkQuality.Full);

            if (result.TryGetProperty("records", out var recordsEl) && recordsEl.ValueKind == JsonValueKind.Array)
            {
                var values = new List<TagValue>();
                foreach (var rec in recordsEl.EnumerateArray())
                {
                    var source = rec.GetProperty("source").GetString()!;
                    var tag = rec.GetProperty("tag").GetString()!;
                    var kindStr = rec.GetProperty("kind").GetString()!;
                    var kind = Enum.Parse<DataKind>(kindStr, true);

                    object? value = null;
                    if (rec.TryGetProperty("value", out var vProp))
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

                    var ts = rec.TryGetProperty("ts", out var tsProp) ? DateTimeOffset.Parse(tsProp.GetString()!) : req.FromTs;
                    var msgId = rec.TryGetProperty("msgId", out var mProp) ? mProp.GetUInt32() : 0u;

                    values.Add(new TagValue { Source = source, Tag = tag, Kind = kind, Value = value, Timestamp = ts, MsgId = msgId });
                }

                chunk.AddRange(values);
                _logger.LogDebug("Chunk {ChunkId} received {Count} records from upstream", req.ChunkId, values.Count);
            }

            chunk.Seal();

            _bufferManager.GetOrCreateBuffer(req.SourceId).ReplaceChunk(chunk);

            OnChunkTransferred?.Invoke(chunk);

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
