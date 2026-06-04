using Bridge.Core.Model;
using Bridge.Core.Services;
using Bridge.Storage;

namespace Bridge.Host.Endpoints;

public static class QueryEndpoints
{
    public static IEndpointRouteBuilder MapQueryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sources/{source}");

        g.MapGet("/telemetry", (string source, string? tags, string? from, string? to, int? limit, BufferManager bufMgr) =>
        {
            var buf = bufMgr.GetBuffer(source);
            if (buf is null) return Results.NotFound(new { error = "SOURCE_NOT_FOUND" });

            var (fromTs, toTs) = ParseRange(from, to);
            var tagArr = ParseTags(tags);
            var data = buf.QueryTelemetry(tagArr, fromTs, toTs, limit ?? 1000);
            var quality = buf.GetQuality(fromTs, toTs);
            var truncated = data.Count >= (limit ?? 1000);

            return Results.Ok(new
            {
                kind = "telemetry",
                quality,
                data = GroupByTag(data),
                from = fromTs,
                to = toTs,
                truncated,
                availableFrom = buf.OldestTimestamp
            });
        });

        g.MapGet("/events", (string source, string? tags, string? from, string? to, int? limit, BufferManager bufMgr) =>
        {
            var buf = bufMgr.GetBuffer(source);
            if (buf is null) return Results.NotFound(new { error = "SOURCE_NOT_FOUND" });

            var (fromTs, toTs) = ParseRange(from, to);
            var tagArr = ParseTags(tags);
            var data = buf.QueryEvents(tagArr, fromTs, toTs, limit ?? 1000);
            var quality = buf.GetQuality(fromTs, toTs);

            return Results.Ok(new
            {
                kind = "event",
                quality,
                data = GroupByTag(data),
                from = fromTs,
                to = toTs,
                truncated = data.Count >= (limit ?? 1000)
            });
        });

        g.MapGet("/alarms", (string source, string? tags, string? from, string? to, int? limit, BufferManager bufMgr) =>
        {
            var buf = bufMgr.GetBuffer(source);
            if (buf is null) return Results.NotFound(new { error = "SOURCE_NOT_FOUND" });

            var (fromTs, toTs) = ParseRange(from, to);
            var tagArr = ParseTags(tags);
            var data = buf.QueryAlarms(tagArr, fromTs, toTs, limit ?? 1000);
            var quality = buf.GetQuality(fromTs, toTs);

            return Results.Ok(new
            {
                kind = "alarm",
                quality,
                data = GroupByTag(data),
                from = fromTs,
                to = toTs,
                truncated = data.Count >= (limit ?? 1000)
            });
        });

        g.MapGet("/export", async (string source, string? tags, string? from, string? to, string? kind, string? format, BufferManager bufMgr) =>
        {
            var buf = bufMgr.GetBuffer(source);
            if (buf is null) return Results.NotFound(new { error = "SOURCE_NOT_FOUND" });

            var (fromTs, toTs) = ParseRange(from, to);
            var tagArr = ParseTags(tags);
            var dataKind = kind?.ToLowerInvariant() ?? "telemetry";

            var data = dataKind switch
            {
                "event" or "events" => buf.QueryEvents(tagArr, fromTs, toTs, 100000),
                "alarm" or "alarms" => buf.QueryAlarms(tagArr, fromTs, toTs, 100000),
                _ => buf.QueryTelemetry(tagArr, fromTs, toTs, 100000)
            };

            var ms = new MemoryStream();
            await CsvExporter.WriteAsync(ms, data);
            ms.Position = 0;
            return Results.File(ms, "text/csv", $"{source}_{dataKind}_{fromTs:yyyyMMdd}.csv");
        });

        g.MapGet("/chunks", (string source, BufferManager bufMgr) =>
        {
            var buf = bufMgr.GetBuffer(source);
            if (buf is null) return Results.NotFound(new { error = "SOURCE_NOT_FOUND" });
            return Results.Ok(buf.GetChunkInfos());
        });

        return app;
    }

    private static (DateTimeOffset from, DateTimeOffset to) ParseRange(string? from, string? to)
    {
        var fromTs = from is not null ? DateTimeOffset.Parse(from) : DateTimeOffset.UtcNow.AddHours(-1);
        var toTs = to is not null ? DateTimeOffset.Parse(to) : DateTimeOffset.UtcNow;
        return (fromTs, toTs);
    }

    private static string[]? ParseTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return null;
        return tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    #pragma warning disable CA1859
    private static object GroupByTag(IReadOnlyList<TagValue> data)
    #pragma warning restore CA1859
    {
        return data.GroupBy(v => v.Tag).Select(g => new
        {
            tag = g.Key,
            values = g.Select(v => new { v = v.Value, ts = v.Timestamp, msgId = v.MsgId }).ToList()
        }).ToList();
    }
}
