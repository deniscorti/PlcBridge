using Bridge.Core.Model;
using Bridge.Storage;
using Bridge.Storage.Parquet;
using Microsoft.AspNetCore.Mvc;

namespace Bridge.Host.Endpoints;

public static class ChannelEndpoints
{
    public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sources/{source}/load-channels", async (
            string source,
            [FromBody] LoadChannelsRequest req,
            [FromServices] ParquetStorage? storage) =>
        {
            if (storage is null)
                return Results.BadRequest(new { error = "STORAGE_DISABLED" });

            var historicalPath = storage.HistoricalPath;
            if (historicalPath is null)
                return Results.BadRequest(new { error = "HISTORICAL_PATH_NOT_CONFIGURED" });

            var from = DateTimeOffset.Parse(req.From);
            var to = DateTimeOffset.Parse(req.To);
            var kind = DataKind.Telemetry; // loadChannels is primarily for telemetry

            var files = storage.ListHistoricalFiles(source, kind, from, to);
            if (files.Count == 0)
                return Results.Ok(new LoadChannelsResponse(source, req.From, req.To, 0, 0, false, null, []));

            // Read matching data from Parquet files
            var allValues = new List<TagValue>();
            foreach (var f in files)
            {
                var values = await ParquetStorage.ReadFileAsync(f.FullPath, req.Tags, from, to);
                allValues.AddRange(values);
            }

            if (allValues.Count == 0)
                return Results.Ok(new LoadChannelsResponse(source, req.From, req.To, 0, 0, false, null, []));

            var totalPoints = allValues.Count;

            // Apply downsampling if resolution specified
            if (req.Resolution is > 0)
                allValues = Downsampler.MinMaxBucketMultiTag(allValues, req.Resolution.Value);

            // Group by tag
            var data = allValues
                .GroupBy(v => v.Tag)
                .Select(g => new LoadChannelsTagData(
                    g.Key,
                    totalPoints > 0 ? allValues.Count(v => v.Tag == g.Key) : 0, // approximate raw count
                    g.OrderBy(v => v.Timestamp)
                     .Select(v => new TagPoint(v.Value, v.Timestamp, v.MsgId))
                     .ToList()))
                .ToList();

            var returnedPoints = data.Sum(d => d.Values.Count);

            return Results.Ok(new LoadChannelsResponse(
                source, req.From, req.To,
                totalPoints, returnedPoints,
                req.Resolution.HasValue && totalPoints > returnedPoints,
                req.Resolution,
                data));
        });

        return app;
    }
}

public sealed record LoadChannelsRequest(
    string[]? Tags,
    string From,
    string To,
    int? Resolution = null);

public sealed record LoadChannelsResponse(
    string Source,
    string From,
    string To,
    int TotalPoints,
    int ReturnedPoints,
    bool Downsampled,
    int? Resolution,
    List<LoadChannelsTagData> Data);

public sealed record LoadChannelsTagData(
    string Tag,
    int RawCount,
    List<TagPoint> Values);

public sealed record TagPoint(object? V, DateTimeOffset Ts, uint MsgId);
