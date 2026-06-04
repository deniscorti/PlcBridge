using System.Text.Json;
using Bridge.Core.Buffer;
using Bridge.Core.Model;
using Bridge.Core.Services;
using Bridge.InterBridge.WsClient;
using Microsoft.Extensions.Logging;

namespace Bridge.InterBridge.ChunkTransfer;

/// <summary>
/// Manages background chunk transfer from DataService to DataServer.
/// Downloads Parquet files via HTTP (efficient binary transfer) instead of
/// serializing millions of records as JSON via WebSocket.
/// Prioritizes newest chunks first so the most recent data is available quickly.
/// </summary>
public sealed class ChunkTransferService : IDisposable
{
    private readonly BufferManager _bufferManager;
    private readonly string _archivePath;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly object _queueLock = new();
    private readonly List<ChunkTransferRequest> _pending = [];
    private bool _processing;
    private int _completedTransfers;
    private long _totalTransferMs;

    public int PendingCount { get { lock (_queueLock) return _pending.Count; } }
    public int CompletedCount => _completedTransfers;
    public long AvgTransferMs => _completedTransfers > 0 ? _totalTransferMs / _completedTransfers : 0;

    /// <summary>Fired when Parquet files for a chunk have been downloaded and saved locally.
    /// The string array contains the local file paths written to archivePath.</summary>
    public event Action<string[], ChunkTransferInfo>? OnChunkFilesReceived;

    public ChunkTransferService(BufferManager bufferManager, string archivePath, ILogger<ChunkTransferService> logger)
    {
        _bufferManager = bufferManager;
        _archivePath = archivePath;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        Directory.CreateDirectory(_archivePath);
    }

    public void Dispose() => _httpClient.Dispose();

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

        // Extract Parquet file names from chunkReady (new protocol)
        var files = new List<string>();
        if (chunkInfo.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in filesEl.EnumerateArray())
            {
                if (f.GetString() is string name)
                    files.Add(name);
            }
        }

        _logger.LogInformation("Chunk ready: {ChunkId} for source {Source} ({Records} records, {FileCount} files, {From} → {To})",
            chunkId, sourceId, records, files.Count, fromTs, toTs);

        // Derive upstream HTTP base URL from the WS URL (ws://host:port/ws → http://host:port)
        var wsUrl = client.Url;
        var httpBaseUrl = wsUrl
            .Replace("wss://", "https://")
            .Replace("ws://", "http://");
        // Remove /ws path suffix
        var wsPathIdx = httpBaseUrl.LastIndexOf("/ws", StringComparison.OrdinalIgnoreCase);
        if (wsPathIdx > 0) httpBaseUrl = httpBaseUrl[..wsPathIdx];

        var req = new ChunkTransferRequest(sourceId, chunkId, fromTs, toTs, files, httpBaseUrl);

        lock (_queueLock)
        {
            // Insert sorted: newest (highest ToTs) first
            var idx = _pending.FindIndex(r2 => r2.ToTs < req.ToTs);
            if (idx < 0)
                _pending.Add(req); // oldest so far, add at end
            else
                _pending.Insert(idx, req);
        }

        _ = ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
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

        bool hasMore;
        lock (_queueLock) hasMore = _pending.Count > 0;
        if (hasMore) _ = ProcessQueueAsync();
    }

    private async Task TransferChunkAsync(ChunkTransferRequest req)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (req.Files.Count == 0)
            {
                _logger.LogWarning("Chunk {ChunkId} has no Parquet files to download — skipping", req.ChunkId);
                return;
            }

            _logger.LogInformation("Downloading {FileCount} Parquet files for chunk {ChunkId} from {BaseUrl}",
                req.Files.Count, req.ChunkId, req.HttpBaseUrl);

            var downloadedPaths = new List<string>();

            foreach (var fileName in req.Files)
            {
                var url = $"{req.HttpBaseUrl}/api/archives/download?file={Uri.EscapeDataString(fileName)}";

                using var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to download {FileName}: HTTP {StatusCode}", fileName, response.StatusCode);
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync();
                var localPath = Path.Combine(_archivePath, fileName);
                await File.WriteAllBytesAsync(localPath, bytes);
                downloadedPaths.Add(localPath);

                _logger.LogDebug("Downloaded and saved {FileName} ({Size} bytes)", fileName, bytes.Length);
            }

            sw.Stop();
            Interlocked.Increment(ref _completedTransfers);
            Interlocked.Add(ref _totalTransferMs, sw.ElapsedMilliseconds);

            if (downloadedPaths.Count > 0)
            {
                OnChunkFilesReceived?.Invoke(
                    downloadedPaths.ToArray(),
                    new ChunkTransferInfo(req.SourceId, req.ChunkId, req.FromTs, req.ToTs));
            }

            _logger.LogInformation("Chunk {ChunkId} transferred ({FileCount} files) in {Ms}ms",
                req.ChunkId, downloadedPaths.Count, sw.ElapsedMilliseconds);
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
        List<string> Files, string HttpBaseUrl);
}

/// <summary>Info about a completed chunk transfer.</summary>
public sealed record ChunkTransferInfo(
    string SourceId, string ChunkId,
    DateTimeOffset FromTs, DateTimeOffset ToTs);
