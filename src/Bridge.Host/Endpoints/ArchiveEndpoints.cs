using Bridge.Core.Buffer;
using Bridge.Core.Services;
using Bridge.Storage.Parquet;
using Microsoft.AspNetCore.Mvc;

namespace Bridge.Host.Endpoints;

public static class ArchiveEndpoints
{
    public static IEndpointRouteBuilder MapArchiveEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/archives", (string? source, [FromServices] ParquetStorage? storage) =>
        {
            if (storage is null) return Results.BadRequest(new { error = "STORAGE_DISABLED" });
            var files = storage.ListFiles(source);
            return Results.Ok(files.Select(f => new { file = f.FileName, size = f.SizeBytes }));
        });

        g.MapPost("/archives/load", async (ArchiveLoadRequest req, [FromServices] ParquetStorage? storage, [FromServices] BufferManager? bufMgr) =>
        {
            if (storage is null || bufMgr is null)
                return Results.BadRequest(new { error = "STORAGE_DISABLED" });

            var path = storage.ResolvePath(req.File);
            if (path is null) return Results.NotFound(new { error = "FILE_NOT_FOUND" });

            var values = await ParquetStorage.ReadFileAsync(path);
            if (values.Count == 0) return Results.Ok(new { loaded = 0 });

            var source = values[0].Source;
            var buf = bufMgr.GetOrCreateBuffer(source);
            var fromTs = values.Min(v => v.Timestamp);
            var toTs = values.Max(v => v.Timestamp);

            var chunk = new Chunk(source, fromTs, toTs, ChunkQuality.Loaded);
            chunk.AddRange(values);
            chunk.Seal();
            buf.InsertChunk(chunk);

            return Results.Ok(new { loaded = values.Count, source, fromTs, toTs });
        });

        return app;
    }
}

public sealed record ArchiveLoadRequest(string Source, string File);
