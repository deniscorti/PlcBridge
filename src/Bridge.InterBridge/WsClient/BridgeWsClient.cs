using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bridge.Core.Model;
using Microsoft.Extensions.Logging;

namespace Bridge.InterBridge.WsClient;

/// <summary>
/// WebSocket client that connects to an upstream Bridge node (DataProvider or DataService),
/// subscribes to tags, receives push data, and relays commands.
/// </summary>
public sealed class BridgeWsClient : IAsyncDisposable
{
    private readonly BridgeWsClientOptions _opts;
    private readonly ILogger _logger;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private int _msgId;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();

    public string SourceId { get; }
    public string Url => _opts.Url;
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>Fired when a tag value arrives from the upstream bridge.</summary>
    public event Action<TagValue>? OnValue;

    /// <summary>Fired when connection state changes.</summary>
    public event Action<string, bool>? OnConnectionChanged;

    /// <summary>Fired when a sealed chunk is ready for transfer (DataService→DataServer).</summary>
    public event Action<JsonElement>? OnChunkReady;

    /// <summary>Fired after a successful (re)connect, before the receive loop starts. Used by the aggregator to re-push subscriptions.</summary>
    public event Action<BridgeWsClient>? OnConnected;

    /// <summary>Fired when upstream sources/tags are discovered via getSources. Args: list of (sourceId, tagName, dataKind).</summary>
    public event Action<List<(string Source, string Tag, DataKind Kind)>>? OnTagsDiscovered;

    public BridgeWsClient(BridgeWsClientOptions opts, ILogger logger)
    {
        _opts = opts;
        _logger = logger;
        SourceId = opts.Id;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await ConnectInternalAsync(_cts.Token);
            _receiveTask = ReceiveLoopAsync(_cts.Token);
            _heartbeatTask = HeartbeatLoopAsync(_cts.Token);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial connection to upstream {Url} failed for source {Source}, will retry", _opts.Url, SourceId);
            _ = ReconnectAsync(_cts.Token);
        }
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            try { if (_receiveTask is not null) await _receiveTask; } catch { /* expected */ }
            try { if (_heartbeatTask is not null) await _heartbeatTask; } catch { /* expected */ }
        }
        if (_ws is not null && _ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { /* best effort */ }
        }
    }

    /// <summary>Send a command to the upstream bridge and wait for response.</summary>
    public async Task<JsonElement> SendCommandAsync(string command, string? source = null, JsonElement? parameters = null, int timeoutMs = 5000)
    {
        var id = $"cmd-{Interlocked.Increment(ref _msgId)}";
        var msg = new Dictionary<string, object?> { ["op"] = "sourceCommand", ["id"] = id, ["command"] = command };
        if (source is not null) msg["source"] = source;
        if (parameters is not null) msg["params"] = parameters;

        var tcs = new TaskCompletionSource<JsonElement>();
        _pendingRequests[id] = tcs;

        try
        {
            await SendJsonAsync(msg);
            using var timeout = new CancellationTokenSource(timeoutMs);
            timeout.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    /// <summary>Request a chunk transfer from upstream (DataServer→DataService).</summary>
    public async Task<JsonElement> RequestChunkAsync(string chunkId, string? source = null, int timeoutMs = 30000)
    {
        var id = $"cr-{Interlocked.Increment(ref _msgId)}";
        var msg = new Dictionary<string, object?> { ["op"] = "chunkRequest", ["id"] = id, ["chunkId"] = chunkId };
        if (source is not null) msg["source"] = source;

        var tcs = new TaskCompletionSource<JsonElement>();
        _pendingRequests[id] = tcs;

        try
        {
            await SendJsonAsync(msg);
            using var timeout = new CancellationTokenSource(timeoutMs);
            timeout.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    /// <summary>Subscribe to additional tags on the upstream bridge (incremental).</summary>
    /// <param name="tags">Tags to subscribe.</param>
    /// <param name="source">Source name on the upstream (discovered via getSources). If null, omitted from the message.</param>
    public async Task SubscribeTagsAsync(IReadOnlyList<string> tags, string? source = null)
    {
        if (!IsConnected || tags.Count == 0) return;
        var msg = new Dictionary<string, object?>
        {
            ["op"] = "subscribe",
            ["id"] = $"sub-{Interlocked.Increment(ref _msgId)}",
            ["tags"] = tags
        };
        if (source is not null) msg["source"] = source;
        if (_opts.DownsampleMs > 0) msg["downsampleMs"] = _opts.DownsampleMs;
        if (_opts.Compression != "none") msg["compression"] = _opts.Compression;
        if (_opts.BatchMode) msg["batchMode"] = true;
        if (_opts.ChunkSync) msg["chunkSync"] = true;

        await SendJsonAsync(msg);
    }

    /// <summary>Unsubscribe from specific tags on the upstream bridge.</summary>
    public async Task UnsubscribeTagsAsync(IReadOnlyList<string> tags, string? source = null)
    {
        if (!IsConnected || tags.Count == 0) return;
        var msg = new Dictionary<string, object?>
        {
            ["op"] = "unsubscribe",
            ["id"] = $"unsub-{Interlocked.Increment(ref _msgId)}",
            ["tags"] = tags
        };
        if (source is not null) msg["source"] = source;
        await SendJsonAsync(msg);
    }

    /// <summary>Query recent telemetry from upstream for backfill. Returns the raw response.</summary>
    public async Task<JsonElement> QueryTelemetryAsync(string[] tags, DateTimeOffset from, DateTimeOffset to, string? source = null, int limit = 3000, int timeoutMs = 10000)
    {
        var id = $"qt-{Interlocked.Increment(ref _msgId)}";
        var msg = new Dictionary<string, object?>
        {
            ["op"] = "queryTelemetry",
            ["id"] = id,
            ["tags"] = tags,
            ["from"] = from,
            ["to"] = to,
            ["limit"] = limit
        };
        if (source is not null) msg["source"] = source;

        var tcs = new TaskCompletionSource<JsonElement>();
        _pendingRequests[id] = tcs;

        try
        {
            await SendJsonAsync(msg);
            using var timeout = new CancellationTokenSource(timeoutMs);
            timeout.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    private async Task DiscoverTagsAsync()
    {
        try
        {
            var id = $"disc-{Interlocked.Increment(ref _msgId)}";
            var msg = new Dictionary<string, object?> { ["op"] = "getSources", ["id"] = id };
            var tcs = new TaskCompletionSource<JsonElement>();
            _pendingRequests[id] = tcs;

            await SendJsonAsync(msg);
            using var timeout = new CancellationTokenSource(10000);
            timeout.Token.Register(() => tcs.TrySetCanceled());
            var result = await tcs.Task;
            _pendingRequests.TryRemove(id, out _);

            if (result.TryGetProperty("sources", out var sourcesArr) && sourcesArr.ValueKind == JsonValueKind.Array)
            {
                var discovered = new List<(string Source, string Tag, DataKind Kind)>();
                foreach (var src in sourcesArr.EnumerateArray())
                {
                    var sourceId = src.GetProperty("id").GetString()!;
                    if (src.TryGetProperty("tags", out var tagsArr) && tagsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tagEl in tagsArr.EnumerateArray())
                        {
                            var tagName = tagEl.GetProperty("name").GetString()!;
                            var kindStr = tagEl.TryGetProperty("kind", out var k) ? k.GetString() : "Telemetry";
                            var kind = Enum.TryParse<DataKind>(kindStr, true, out var dk) ? dk : DataKind.Telemetry;
                            discovered.Add((sourceId, tagName, kind));
                        }
                    }
                }

                if (discovered.Count > 0)
                {
                    _logger.LogInformation("Discovered {Count} tags from upstream for source {Source}", discovered.Count, SourceId);
                    OnTagsDiscovered?.Invoke(discovered);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tag discovery failed for source {Source}", SourceId);
        }
    }

    private async Task ConnectInternalAsync(CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        var uri = new Uri(_opts.Url + $"?apiKey={_opts.ApiKey}");

        _logger.LogInformation("Connecting to upstream bridge {Url} for source {Source}", _opts.Url, SourceId);
        await _ws.ConnectAsync(uri, ct);
        _logger.LogInformation("Connected to upstream bridge for source {Source}", SourceId);
        OnConnectionChanged?.Invoke(SourceId, true);

        // Discover upstream tags via getSources
        if (OnTagsDiscovered is not null)
            _ = DiscoverTagsAsync();

        // If selective subscription is enabled, let the aggregator handle subscriptions via OnConnected.
        // Otherwise, subscribe immediately (legacy/DataService mode).
        if (_opts.SelectiveSubscription)
        {
            // Always subscribe to chunkSync if enabled (chunks are always needed)
            if (_opts.ChunkSync)
            {
                var chunkMsg = new Dictionary<string, object?>
                {
                    ["op"] = "subscribe",
                    ["id"] = $"sub-{Interlocked.Increment(ref _msgId)}",
                    ["tags"] = Array.Empty<string>(), // no live tags
                    ["chunkSync"] = true
                };
                await SendJsonAsync(chunkMsg);
            }

            // Notify the aggregator to re-push current subscriptions
            OnConnected?.Invoke(this);
        }
        else
        {
            // Legacy mode: subscribe to configured tags (or ALL if AutoDiscovery)
            // source omitted = subscribe to all sources on the upstream
            var subMsg = new Dictionary<string, object?>
            {
                ["op"] = "subscribe",
                ["id"] = $"sub-{Interlocked.Increment(ref _msgId)}",
                ["tags"] = _opts.AutoDiscovery ? new[] { "ALL" } : _opts.Tags
            };
            if (_opts.DownsampleMs > 0) subMsg["downsampleMs"] = _opts.DownsampleMs;
            if (_opts.Compression != "none") subMsg["compression"] = _opts.Compression;
            if (_opts.BatchMode) subMsg["batchMode"] = true;
            if (_opts.ChunkSync) subMsg["chunkSync"] = true;

            await SendJsonAsync(subMsg);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;

                var json = Encoding.UTF8.GetString(ms.ToArray());
                ProcessMessage(json);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex)
            {
                _logger.LogWarning(ex, "WS receive error for source {Source}", SourceId);
                break;
            }
        }

        OnConnectionChanged?.Invoke(SourceId, false);

        // Reconnect loop
        if (!ct.IsCancellationRequested)
            _ = ReconnectAsync(ct);
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        var delay = 1000;
        while (!ct.IsCancellationRequested)
        {
            _logger.LogInformation("Reconnecting to upstream bridge for source {Source} in {Delay}ms", SourceId, delay);
            try
            {
                await Task.Delay(delay, ct);
                _ws?.Dispose();
                await ConnectInternalAsync(ct);
                _receiveTask = ReceiveLoopAsync(ct);
                _heartbeatTask = HeartbeatLoopAsync(ct);
                delay = 1000;
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnect failed for source {Source}", SourceId);
                delay = Math.Min(delay * 2, 30000);
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_opts.HeartbeatIntervalMs, ct);
                if (_ws?.State == WebSocketState.Open)
                    await SendJsonAsync(new { op = "ping" });
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore heartbeat errors */ }
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            // Handle response to pending requests
            if (type == "response" && root.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString();
                if (id is not null && _pendingRequests.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(root.Clone());
                    return;
                }
            }

            // Handle chunk ready notification
            if (type == "chunkReady")
            {
                OnChunkReady?.Invoke(root.Clone());
                return;
            }

            // Handle sourcesChanged notification — re-discover tags from upstream
            if (type == "sourcesChanged")
            {
                if (OnTagsDiscovered is not null)
                {
                    _logger.LogInformation("Upstream sources changed for {Source}, re-discovering tags", SourceId);
                    _ = DiscoverTagsAsync();
                }
                return;
            }

            // Handle push data
            if (type is "telemetry" or "event" or "alarm")
            {
                var source = root.GetProperty("source").GetString()!;
                var tag = root.GetProperty("tag").GetString()!;
                var kind = type switch
                {
                    "telemetry" => DataKind.Telemetry,
                    "event" => DataKind.Event,
                    "alarm" => DataKind.Alarm,
                    _ => DataKind.Telemetry
                };

                object? value = null;
                if (root.TryGetProperty("value", out var vProp))
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
                else if (kind == DataKind.Alarm && root.TryGetProperty("active", out var aProp))
                {
                    value = aProp.GetBoolean();
                }

                var ts = root.TryGetProperty("ts", out var tsProp) ? DateTimeOffset.Parse(tsProp.GetString()!) : DateTimeOffset.UtcNow;
                var msgId = root.TryGetProperty("msgId", out var mProp) ? mProp.GetUInt32() : 0u;

                OnValue?.Invoke(new TagValue
                {
                    Source = source,
                    Tag = tag,
                    Kind = kind,
                    Value = value,
                    Timestamp = ts,
                    MsgId = msgId
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process WS message from upstream");
        }
    }

    private async Task SendJsonAsync(object msg)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var json = JsonSerializer.SerializeToUtf8Bytes(msg, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await _ws.SendAsync(json, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}

public sealed class BridgeWsClientOptions
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public string? ApiKey { get; init; }

    /// <summary>When true, discover all tags from the upstream node (ignores Tags).</summary>
    public bool AutoDiscovery { get; init; } = true;

    /// <summary>Explicit list of tags to subscribe. Ignored when AutoDiscovery is true.</summary>
    public string[] Tags { get; init; } = [];

    public int DownsampleMs { get; init; }
    public string Compression { get; init; } = "none";
    public bool BatchMode { get; init; }
    public bool ChunkSync { get; init; }
    public int HeartbeatIntervalMs { get; init; } = 10000;
    public int ChunkSyncMaxBandwidthKbps { get; init; } = 100;

    /// <summary>When true, subscriptions are managed by the UpstreamSubscriptionAggregator instead of subscribing to all tags at connect.</summary>
    public bool SelectiveSubscription { get; init; }
}
