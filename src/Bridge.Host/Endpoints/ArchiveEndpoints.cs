using Bridge.Core.Buffer;
using Bridge.Core.Model;
using Bridge.Core.Services;
using Bridge.Storage.Parquet;
using Microsoft.AspNetCore.Mvc;

namespace Bridge.Host.Endpoints;

public static class ArchiveEndpoints
{
    public static IEndpointRouteBuilder MapArchiveEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/archives", (string? source, string? kind, [FromServices] ParquetStorage? storage) =>
        {
            if (storage is null) return Results.BadRequest(new { error = "STORAGE_DISABLED" });

            DataKind? dataKind = kind is not null ? Enum.Parse<DataKind>(kind, true) : null;
            var files = storage.ListFiles(source, dataKind);

            return Results.Ok(files.Select(f =>
            {
                var parsed = ParquetStorage.ParseFileName(Path.GetFileNameWithoutExtension(f.FileName));
                var timeRange = ParquetStorage.ParseTimeRange(f.FileName);
                var msgRange = ParquetStorage.ParseMsgIdRange(f.FileName);

                return new
                {
                    file = f.FileName,
                    size = f.SizeBytes,
                    source = parsed?.source,
                    kind = parsed?.kind.ToString().ToLowerInvariant(),
                    fromTs = timeRange?.from,
                    toTs = timeRange?.to,
                    firstMsgId = msgRange?.firstMsgId,
                    lastMsgId = msgRange?.lastMsgId
                };
            }));
        });

        g.MapPost("/archives/load", async (ArchiveLoadRequest req, [FromServices] ParquetStorage? storage, [FromServices] BufferManager? bufMgr) =>
        {
            if (storage is null || bufMgr is null)
                return Results.BadRequest(new { error = "STORAGE_DISABLED" });

            var path = storage.ResolvePath(req.File);
            if (path is null) return Results.NotFound(new { error = "FILE_NOT_FOUND" });

            var values = await ParquetStorage.ReadFileAsync(path, req.Tags);
            if (values.Count == 0) return Results.Ok(new { loaded = 0 });

            var source = req.Source ?? values[0].Source;
            var buf = bufMgr.GetOrCreateBuffer(source);
            var fromTs = values.Min(v => v.Timestamp);
            var toTs = values.Max(v => v.Timestamp);

            var chunk = new Chunk(source, fromTs, toTs, ChunkQuality.Loaded);
            chunk.AddRange(values);
            chunk.Seal();
            buf.InsertChunk(chunk);

            return Results.Ok(new { loaded = values.Count, source, fromTs, toTs });
        });

        g.MapPost("/archives/load-range", async (ArchiveLoadRangeRequest req,
            [FromServices] ParquetStorage? storage, [FromServices] BufferManager? bufMgr) =>
        {
            if (storage is null || bufMgr is null)
                return Results.BadRequest(new { error = "STORAGE_DISABLED" });

            var from = DateTimeOffset.Parse(req.From);
            var to = DateTimeOffset.Parse(req.To);
            var kind = req.Kind is not null ? Enum.Parse<DataKind>(req.Kind, true) : DataKind.Telemetry;

            var files = storage.ListFilesInRange(req.Source, kind, from, to);
            if (files.Count == 0)
                return Results.Ok(new { loaded = 0, files = 0, source = req.Source });

            var allValues = new List<TagValue>();
            foreach (var f in files)
            {
                var path = f.FullPath;
                var values = await ParquetStorage.ReadFileAsync(path, req.Tags, from, to);
                allValues.AddRange(values);
            }

            if (allValues.Count == 0)
                return Results.Ok(new { loaded = 0, files = files.Count, source = req.Source });

            var buf = bufMgr.GetOrCreateBuffer(req.Source);
            var actualFrom = allValues.Min(v => v.Timestamp);
            var actualTo = allValues.Max(v => v.Timestamp);

            var chunk = new Chunk(req.Source, actualFrom, actualTo, ChunkQuality.Loaded);
            chunk.AddRange(allValues);
            chunk.Seal();
            buf.InsertChunk(chunk);

            return Results.Ok(new { loaded = allValues.Count, files = files.Count, source = req.Source, fromTs = actualFrom, toTs = actualTo });
        });

        // Download a Parquet file by name (used by DataServer for chunk transfer via HTTP)
        // Uses query parameter ?file=xxx.parquet to avoid ASP.NET routing issues with dots in path
        g.MapGet("/archives/download", (string file, [FromServices] ParquetStorage? storage) =>
        {
            if (storage is null) return Results.BadRequest(new { error = "STORAGE_DISABLED" });
            if (string.IsNullOrEmpty(file)) return Results.BadRequest(new { error = "MISSING_FILE_PARAM" });

            var path = storage.ResolvePath(file);
            if (path is null) return Results.NotFound(new { error = "FILE_NOT_FOUND", file });

            var fullPath = Path.GetFullPath(path);
            return Results.File(fullPath, "application/octet-stream", file);
        });

        return app;
    }
}

public sealed record ArchiveLoadRequest(string? Source, string File, string[]? Tags = null);

public sealed record ArchiveLoadRangeRequest(string Source, string? Kind, string From, string To, string[]? Tags = null);
