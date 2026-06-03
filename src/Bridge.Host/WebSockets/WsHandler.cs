using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bridge.Contracts.Ws;
using Bridge.Core.Abstractions;
using Bridge.Core.Buffer;
using Bridge.Core.Model;
using Bridge.Core.Services;
using Bridge.InterBridge.WsClient;

namespace Bridge.Host.WebSockets;

public static class WsHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static IEndpointRouteBuilder MapBridgeWebSocket(this IEndpointRouteBuilder app, string path)
    {
        app.Map(path, async (HttpContext ctx, WsConnectionManager connMgr, ISubscriptionBroker broker,
            SourceManager srcMgr, BufferManager? bufMgr, CompactLayoutManager layoutMgr) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var connId = connMgr.AddConnection(socket);

            try
            {
                await HandleConnectionAsync(socket, connId, connMgr, broker, srcMgr, bufMgr, layoutMgr, ctx.RequestAborted);
            }
            finally
            {
                broker.UnsubscribeAll(connId);
                layoutMgr.RemoveConnection(connId);
                connMgr.RemoveConnection(connId);
            }
        });

        return app;
    }

    private static async Task HandleConnectionAsync(
        WebSocket socket, string connId, WsConnectionManager connMgr,
        ISubscriptionBroker broker, SourceManager srcMgr, BufferManager? bufMgr,
        CompactLayoutManager layoutMgr, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            using var ms = new MemoryStream();
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;

            var json = Encoding.UTF8.GetString(ms.ToArray());
            await ProcessMessageAsync(json, connId, connMgr, broker, srcMgr, bufMgr, layoutMgr, ct);
        }
    }

    private static async Task ProcessMessageAsync(
        string json, string connId, WsConnectionManager connMgr,
        ISubscriptionBroker broker, SourceManager srcMgr, BufferManager? bufMgr,
        CompactLayoutManager layoutMgr, CancellationToken ct)
    {
        WsClientMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<WsClientMessage>(json, JsonOpts);
        }
        catch
        {
            await connMgr.SendAsync(connId, new WsError { Code = "PARSE_ERROR", Message = "Invalid JSON." }, ct);
            return;
        }
        if (msg is null) return;

        switch (msg)
        {
            case WsPing:
                await connMgr.SendAsync(connId, new WsPong(), ct);
                break;

            case WsSubscribe sub:
                await HandleSubscribeAsync(connId, sub, broker, layoutMgr, connMgr, ct);
                break;

            case WsUnsubscribe unsub:
                await HandleUnsubscribeAsync(connId, unsub, broker, layoutMgr, connMgr, ct);
                break;

            case WsRead read:
                await HandleReadAsync(connId, read, connMgr, srcMgr, ct);
                break;

            case WsWrite write:
                await HandleWriteAsync(connId, write, connMgr, srcMgr, ct);
                break;

            case WsGetSources getSrc:
                await HandleGetSourcesAsync(connId, getSrc, connMgr, srcMgr, bufMgr, ct);
                break;

            case WsGetStatus getSt:
                await connMgr.SendAsync(connId, new WsResponse
                {
                    Id = getSt.Id, Ok = true,
                    Extra = new Dictionary<string, object?> { ["sources"] = srcMgr.GetAllSources().Select(s => s.Id).ToList() }
                }, ct);
                break;

            case WsQueryTelemetry qt:
                HandleQuery(connId, qt.Id, qt.Source, qt.Tags, qt.From, qt.To, qt.Limit, "telemetry", bufMgr, connMgr, ct);
                break;

            case WsQueryEvents qe:
                HandleQuery(connId, qe.Id, qe.Source, qe.Tags, qe.From, qe.To, qe.Limit, "event", bufMgr, connMgr, ct);
                break;

            case WsQueryAlarms qa:
                HandleQuery(connId, qa.Id, qa.Source, qa.Tags, qa.From, qa.To, qa.Limit, "alarm", bufMgr, connMgr, ct);
                break;

            case WsBridgeCommand bc:
                await HandleBridgeCommandAsync(connId, bc, connMgr, bufMgr, ct);
                break;

            case WsSourceCommand sc:
                await HandleSourceCommandAsync(connId, sc, connMgr, srcMgr, ct);
                break;

            case WsAckAlarm ack:
                await HandleAckAlarmAsync(connId, ack, connMgr, srcMgr, ct);
                break;

            case WsChunkRequest cr:
                await HandleChunkRequestAsync(connId, cr, connMgr, bufMgr, ct);
                break;

            default:
                await connMgr.SendAsync(connId, new WsError { Id = msg.Id, Code = "UNSUPPORTED_OP", Message = "Not supported in current mode." }, ct);
                break;
        }
    }

    private static async Task HandleSubscribeAsync(string connId, WsSubscribe sub,
        ISubscriptionBroker broker, CompactLayoutManager layoutMgr, WsConnectionManager connMgr, CancellationToken ct)
    {
        if (sub.Kinds is { Length: > 0 })
        {
            var kinds = sub.Kinds.Select(k => Enum.Parse<DataKind>(k, true)).ToList();
            broker.Subscribe(connId, sub.Source, [], kinds);
            await connMgr.SendAsync(connId, new WsResponse { Id = sub.Id, Ok = true }, ct);
            return;
        }

        var tags = sub.Tags ?? ["ALL"];
        broker.Subscribe(connId, sub.Source, tags);

        if (sub.Compact && sub.Source is not null && !tags.Contains("ALL"))
        {
            // Compact mode: return a layout mapping tag positions
            var layout = layoutMgr.EnableCompact(connId, sub.Source, tags);
            await connMgr.SendAsync(connId, new WsResponse
            {
                Id = sub.Id, Ok = true,
                Extra = new Dictionary<string, object?>
                {
                    ["compact"] = true,
                    ["layout"] = new { source = layout.Source, layoutId = layout.LayoutId, tags = layout.Tags }
                }
            }, ct);
        }
        else if (sub.Compact && layoutMgr.IsCompact(connId) && sub.Source is not null)
        {
            // Adding tags to existing compact subscription
            var layout = layoutMgr.AddTags(connId, sub.Source, tags);
            if (layout is not null)
            {
                await connMgr.SendAsync(connId, new WsResponse
                {
                    Id = sub.Id, Ok = true,
                    Extra = new Dictionary<string, object?>
                    {
                        ["compact"] = true,
                        ["layout"] = new { source = layout.Source, layoutId = layout.LayoutId, tags = layout.Tags }
                    }
                }, ct);
            }
            else
            {
                await connMgr.SendAsync(connId, new WsResponse { Id = sub.Id, Ok = true }, ct);
            }
        }
        else
        {
            await connMgr.SendAsync(connId, new WsResponse { Id = sub.Id, Ok = true }, ct);
        }
    }

    private static async Task HandleUnsubscribeAsync(string connId, WsUnsubscribe unsub,
        ISubscriptionBroker broker, CompactLayoutManager layoutMgr, WsConnectionManager connMgr, CancellationToken ct)
    {
        broker.Unsubscribe(connId, unsub.Source, unsub.Tags);

        // Update compact layout if active
        if (unsub.Source is not null && layoutMgr.IsCompact(connId))
        {
            var layout = layoutMgr.RemoveTags(connId, unsub.Source, unsub.Tags);
            if (layout is not null)
            {
                await connMgr.SendAsync(connId, new
                {
                    type = "layoutUpdate",
                    source = layout.Source,
                    layoutId = layout.LayoutId,
                    tags = layout.Tags
                }, ct);
            }
        }

        await connMgr.SendAsync(connId, new WsResponse { Id = unsub.Id, Ok = true }, ct);
    }

    private static async void HandleQuery(string connId, string? id, string source, string[]? tags,
        string? from, string? to, int limit, string kind,
        BufferManager? bufMgr, WsConnectionManager connMgr, CancellationToken ct)
    {
        if (bufMgr is null)
        {
            await connMgr.SendAsync(connId, new WsError { Id = id, Code = "NO_BUFFER", Message = "Buffer not available in this mode." }, ct);
            return;
        }

        var buf = bufMgr.GetBuffer(source);
        if (buf is null)
        {
            await connMgr.SendAsync(connId, new WsError { Id = id, Code = "SOURCE_NOT_FOUND", Message = $"Source '{source}' not found." }, ct);
            return;
        }

        var fromTs = from is not null ? DateTimeOffset.Parse(from) : DateTimeOffset.UtcNow.AddHours(-1);
        var toTs = to is not null ? DateTimeOffset.Parse(to) : DateTimeOffset.UtcNow;

        var data = kind switch
        {
            "event" => buf.QueryEvents(tags, fromTs, toTs, limit),
            "alarm" => buf.QueryAlarms(tags, fromTs, toTs, limit),
            _ => buf.QueryTelemetry(tags, fromTs, toTs, limit)
        };

        var quality = buf.GetQuality(fromTs, toTs);
        var grouped = data.GroupBy(v => v.Tag).Select(g => new
        {
            tag = g.Key,
            values = g.Select(v => new { v = v.Value, ts = v.Timestamp, msgId = v.MsgId }).ToList()
        }).ToList();

        await connMgr.SendAsync(connId, new WsResponse
        {
            Id = id, Ok = true,
            Extra = new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["quality"] = quality,
                ["data"] = grouped,
                ["from"] = fromTs,
                ["to"] = toTs,
                ["truncated"] = data.Count >= limit
            }
        }, ct);
    }

    private static async Task HandleBridgeCommandAsync(string connId, WsBridgeCommand bc,
        WsConnectionManager connMgr, BufferManager? bufMgr, CancellationToken ct)
    {
        switch (bc.Command.ToLowerInvariant())
        {
            case "setchunkduration":
                if (bufMgr is null) { await connMgr.SendAsync(connId, new WsError { Id = bc.Id, Code = "NO_BUFFER", Message = "No buffer." }, ct); return; }
                if (bc.Params?.TryGetValue("minutes", out var minObj) == true)
                {
                    var minutes = Convert.ToInt32(minObj);
                    bufMgr.SetChunkDuration(minutes);
                    await connMgr.SendAsync(connId, new WsResponse
                    {
                        Id = bc.Id, Ok = true,
                        Extra = new Dictionary<string, object?> { ["applied"] = new { chunkDurationMin = minutes }, ["note"] = "Effective from next chunk" }
                    }, ct);
                }
                break;

            case "getsettings":
                await connMgr.SendAsync(connId, new WsResponse
                {
                    Id = bc.Id, Ok = true,
                    Extra = new Dictionary<string, object?>
                    {
                        ["settings"] = new
                        {
                            chunkDurationMin = bufMgr?.ChunkDurationMin,
                            inMemoryMinutes = bufMgr?.InMemoryMinutes
                        }
                    }
                }, ct);
                break;

            default:
                await connMgr.SendAsync(connId, new WsResponse { Id = bc.Id, Ok = true }, ct);
                break;
        }
    }

    private static async Task HandleSourceCommandAsync(string connId, WsSourceCommand sc,
        WsConnectionManager connMgr, SourceManager srcMgr, CancellationToken ct)
    {
        // Try to forward via WsClient (inter-bridge)
        var client = srcMgr.GetWsClient(sc.Source);
        if (client is not null)
        {
            try
            {
                var paramsJson = sc.Params is not null
                    ? JsonSerializer.SerializeToElement(sc.Params)
                    : (JsonElement?)null;
                var result = await client.SendCommandAsync(sc.Command, sc.Source, paramsJson);
                await connMgr.SendAsync(connId, new WsResponse
                {
                    Id = sc.Id, Ok = true,
                    Extra = new Dictionary<string, object?> { ["result"] = result.ToString() }
                }, ct);
            }
            catch (TaskCanceledException)
            {
                await connMgr.SendAsync(connId, new WsError { Id = sc.Id, Code = "TIMEOUT", Message = $"Command timeout for source '{sc.Source}'." }, ct);
            }
            return;
        }

        // Direct input
        var input = srcMgr.GetInputForSource(sc.Source);
        if (input is not null)
        {
            try
            {
                await connMgr.SendAsync(connId, new WsResponse { Id = sc.Id, Ok = true }, ct);
            }
            catch (Exception ex)
            {
                await connMgr.SendAsync(connId, new WsError { Id = sc.Id, Code = "COMMAND_FAILED", Message = ex.Message }, ct);
            }
            return;
        }

        await connMgr.SendAsync(connId, new WsError { Id = sc.Id, Code = "SOURCE_NOT_FOUND", Message = $"Source '{sc.Source}' not found." }, ct);
    }

    private static async Task HandleAckAlarmAsync(string connId, WsAckAlarm ack,
        WsConnectionManager connMgr, SourceManager srcMgr, CancellationToken ct)
    {
        var alarm = srcMgr.GetAlarmService();
        if (alarm is null)
        {
            await connMgr.SendAsync(connId, new WsError { Id = ack.Id, Code = "NO_ALARM_SERVICE", Message = "Alarm service not available." }, ct);
            return;
        }

        if (ack.Tag is not null)
        {
            alarm.Acknowledge(ack.Source, ack.Tag);
        }
        else if (ack.Tags is not null)
        {
            foreach (var tag in ack.Tags)
            {
                if (tag == "ALL")
                    alarm.AcknowledgeAll(ack.Source);
                else
                    alarm.Acknowledge(ack.Source, tag);
            }
        }

        await connMgr.SendAsync(connId, new WsResponse { Id = ack.Id, Ok = true }, ct);
    }

    private static async Task HandleReadAsync(string connId, WsRead read,
        WsConnectionManager connMgr, SourceManager srcMgr, CancellationToken ct)
    {
        var ds = srcMgr.GetSource(read.Source);
        if (ds is null) { await connMgr.SendAsync(connId, new WsError { Id = read.Id, Code = "SOURCE_NOT_FOUND", Message = $"Source '{read.Source}' not found." }, ct); return; }

        var last = ds.GetLastValue(read.Tag);
        if (last is not null)
        {
            await connMgr.SendAsync(connId, new WsResponse { Id = read.Id, Ok = true, Extra = new Dictionary<string, object?> { ["value"] = last.Value, ["ts"] = last.Timestamp } }, ct);
            return;
        }

        var input = srcMgr.GetInputForSource(read.Source);
        if (input is not null)
        {
            try
            {
                var value = await input.ReadTagAsync(read.Tag, ct);
                await connMgr.SendAsync(connId, new WsResponse { Id = read.Id, Ok = true, Extra = new Dictionary<string, object?> { ["value"] = value, ["ts"] = DateTimeOffset.UtcNow } }, ct);
            }
            catch (KeyNotFoundException)
            {
                await connMgr.SendAsync(connId, new WsError { Id = read.Id, Code = "TAG_NOT_FOUND", Message = $"Tag '{read.Tag}' not found." }, ct);
            }
            return;
        }

        await connMgr.SendAsync(connId, new WsError { Id = read.Id, Code = "NO_VALUE", Message = "No value available." }, ct);
    }

    private static async Task HandleWriteAsync(string connId, WsWrite write,
        WsConnectionManager connMgr, SourceManager srcMgr, CancellationToken ct)
    {
        var input = srcMgr.GetInputForSource(write.Source);
        if (input is null) { await connMgr.SendAsync(connId, new WsError { Id = write.Id, Code = "NO_INPUT", Message = "No writable input." }, ct); return; }

        try
        {
            await input.WriteTagAsync(write.Tag, write.Value, ct);
            await connMgr.SendAsync(connId, new WsResponse { Id = write.Id, Ok = true }, ct);
        }
        catch (Exception ex)
        {
            await connMgr.SendAsync(connId, new WsError { Id = write.Id, Code = "WRITE_FAILED", Message = ex.Message }, ct);
        }
    }

    private static async Task HandleChunkRequestAsync(string connId, WsChunkRequest req,
        WsConnectionManager connMgr, BufferManager? bufMgr, CancellationToken ct)
    {
        if (bufMgr is null)
        {
            await connMgr.SendAsync(connId, new WsError { Id = req.Id, Code = "NO_BUFFER", Message = "Buffer not available." }, ct);
            return;
        }

        var buf = bufMgr.GetBuffer(req.Source);
        if (buf is null)
        {
            await connMgr.SendAsync(connId, new WsError { Id = req.Id, Code = "SOURCE_NOT_FOUND", Message = $"Source '{req.Source}' not found." }, ct);
            return;
        }

        var sealed_ = buf.GetSealedChunks();
        var chunk = sealed_.FirstOrDefault(c => c.ChunkId == req.ChunkId);

        if (chunk is null)
        {
            await connMgr.SendAsync(connId, new WsResponse
            {
                Id = req.Id, Ok = false,
                Error = "CHUNK_NOT_FOUND",
                Message = $"Chunk '{req.ChunkId}' not found or not sealed."
            }, ct);
            return;
        }

        var allValues = chunk.GetAllValues();
        var records = allValues.Select(v => new
        {
            source = v.Source,
            tag = v.Tag,
            kind = v.Kind.ToString().ToLowerInvariant(),
            value = v.Value,
            ts = v.Timestamp,
            msgId = v.MsgId
        }).ToList();

        await connMgr.SendAsync(connId, new WsResponse
        {
            Id = req.Id, Ok = true,
            Extra = new Dictionary<string, object?>
            {
                ["chunkId"] = chunk.ChunkId,
                ["source"] = chunk.Source,
                ["fromTs"] = chunk.FromTs,
                ["toTs"] = chunk.ToTs,
                ["firstMsgId"] = chunk.FirstMsgId,
                ["lastMsgId"] = chunk.LastMsgId,
                ["records"] = records
            }
        }, ct);
    }

    private static async Task HandleGetSourcesAsync(string connId, WsGetSources msg,
        WsConnectionManager connMgr, SourceManager srcMgr, BufferManager? bufMgr, CancellationToken ct)
    {
        var sources = srcMgr.GetAllSources().Select(s =>
        {
            var buf = bufMgr?.GetBuffer(s.Id);
            return new
            {
                id = s.Id,
                tags = s.Tags.Values.Select(t => new { name = t.Name, kind = t.Kind.ToString() }).ToList(),
                buffer = buf is not null ? new
                {
                    oldestTs = buf.OldestTimestamp,
                    newestTs = buf.NewestTimestamp,
                    chunkCount = buf.ChunkCount,
                    totalRecords = buf.TotalRecords,
                    oldestMsgId = buf.MsgIdRange.oldest,
                    newestMsgId = buf.MsgIdRange.newest
                } : null
            };
        }).ToList();

        await connMgr.SendAsync(connId, new WsResponse
        {
            Id = msg.Id, Ok = true,
            Extra = new Dictionary<string, object?> { ["sources"] = sources }
        }, ct);
    }
}
