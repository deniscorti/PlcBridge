using Bridge.Core.Services;
using Bridge.Host.Configuration;
using Bridge.Host.WebSockets;
using Bridge.InterBridge.ChunkTransfer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Bridge.Host.Endpoints;

public static class MetricsEndpoints
{
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/metrics", (
            IOptions<BridgeOptions> opts,
            SourceManager srcMgr,
            WsConnectionManager connMgr,
            [FromServices] BufferManager? bufMgr,
            [FromServices] ChunkTransferService? chunkTransfer) =>
        {
            var uptime = (long)(DateTimeOffset.UtcNow - StartTime).TotalSeconds;
            var sources = new Dictionary<string, object>();

            foreach (var ds in srcMgr.GetAllSources())
            {
                var input = srcMgr.GetInputForSource(ds.Id);
                sources[ds.Id] = new
                {
                    connected = input?.IsConnected ?? false,
                    tagCount = ds.Tags.Count
                };
            }

            object? buffer = null;
            if (bufMgr is not null)
            {
                var totalChunks = 0;
                var totalRecords = 0;
                foreach (var sourceId in bufMgr.GetBufferedSources())
                {
                    var buf = bufMgr.GetBuffer(sourceId);
                    if (buf is null) continue;
                    totalChunks += buf.ChunkCount;
                    totalRecords += buf.TotalRecords;
                }
                buffer = new { totalChunks, totalRecords };
            }

            object? chunkSync = null;
            if (chunkTransfer is not null)
            {
                chunkSync = new
                {
                    pendingTransfers = chunkTransfer.PendingCount,
                    completedTransfers = chunkTransfer.CompletedCount,
                    avgTransferMs = chunkTransfer.AvgTransferMs
                };
            }

            return Results.Ok(new
            {
                uptime,
                mode = opts.Value.Mode ?? "Unknown",
                sources,
                buffer,
                chunkSync,
                clients = new
                {
                    wsConnections = connMgr.ConnectionCount
                }
            });
        });

        app.MapGet("/api/bridge/settings", (IOptions<BridgeOptions> opts, [FromServices] BufferManager? bufMgr) =>
        {
            return Results.Ok(new
            {
                mode = opts.Value.Mode ?? "Unknown",
                chunkDurationMin = bufMgr?.ChunkDurationMin,
                inMemoryMinutes = bufMgr?.InMemoryMinutes,
                persistToDisk = opts.Value.Buffer.PersistToDisk
            });
        });

        return app;
    }
}
