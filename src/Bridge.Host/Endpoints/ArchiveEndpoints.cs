using Bridge.Core.Buffer;
using Bridge.Core.Model;
using Bridge.Core.Services;
using Bridge.Storage.Parquet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;

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

        // ── Compact: merge multiple chunks into a single Parquet file ──
        // If source is null/empty, compact all sources found in the time range.
        g.MapPost("/archives/compact", async (ArchiveCompactRequest req, [FromServices] ParquetStorage? storage) =>
        {
            if (storage is null)
                return Results.BadRequest(new { error = "STORAGE_DISABLED" });

            var from = DateTimeOffset.Parse(req.From);
            var to = DateTimeOffset.Parse(req.To);
            DataKind? kind = req.Kind is not null ? Enum.Parse<DataKind>(req.Kind, true) : null;

            if (!string.IsNullOrEmpty(req.Source))
            {
                var mergedFiles = await storage.CompactAsync(req.Source, kind, from, to);
                return Results.Ok(new { mergedFiles, sources = new[] { req.Source }, fromTs = from, toTs = to });
            }

            // Compact all sources: discover from file names
            var allFiles = storage.ListFiles(null, kind);
            var sourceNames = allFiles
                .Select(f => ParquetStorage.ParseFileName(Path.GetFileNameWithoutExtension(f.FileName))?.source)
                .Where(s => s is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var allMerged = new List<string>();
            foreach (var src in sourceNames)
            {
                var merged = await storage.CompactAsync(src!, kind, from, to);
                allMerged.AddRange(merged);
            }

            return Results.Ok(new { mergedFiles = allMerged, sources = sourceNames, fromTs = from, toTs = to });
        });

        // ── Scan historical data path for available Parquet files ──
        g.MapPost("/archives/scan-historical", async ([FromBody] ArchiveScanRequest? req, [FromServices] ParquetStorage? storage) =>
        {
            if (storage is null)
                return Results.BadRequest(new { error = "STORAGE_DISABLED" });
            if (storage.HistoricalPath is null)
                return Results.BadRequest(new { error = "HISTORICAL_PATH_NOT_CONFIGURED" });

            DateTimeOffset? from = req?.From is not null ? DateTimeOffset.Parse(req.From) : null;
            DateTimeOffset? to = req?.To is not null ? DateTimeOffset.Parse(req.To) : null;

            var manifest = await ParquetStorage.ScanDirectoryAsync(storage.HistoricalPath, from, to);

            var grouped = manifest
                .GroupBy(e => (e.Source, e.Kind))
                .Select(g => new
                {
                    source = g.Key.Source,
                    kind = g.Key.Kind.ToString().ToLowerInvariant(),
                    tags = g.SelectMany(e => e.Tags).Distinct().OrderBy(t => t).ToArray(),
                    fileCount = g.Count(),
                    fromTs = g.Min(e => e.FromTs),
                    toTs = g.Max(e => e.ToTs)
                })
                .ToList();

            return Results.Ok(new { path = storage.HistoricalPath, sources = grouped });
        });

        // ── Load historical files into buffer ──
        g.MapPost("/archives/load-historical", async (
            ArchiveLoadHistoricalRequest req,
            [FromServices] ParquetStorage? storage,
            [FromServices] BufferManager? bufMgr,
            [FromServices] SourceManager? srcMgr) =>
        {
            if (storage is null || bufMgr is null)
                return Results.BadRequest(new { error = "STORAGE_DISABLED" });
            if (storage.HistoricalPath is null)
                return Results.BadRequest(new { error = "HISTORICAL_PATH_NOT_CONFIGURED" });

            var from = DateTimeOffset.Parse(req.From);
            var to = DateTimeOffset.Parse(req.To);

            var manifest = await ParquetStorage.ScanDirectoryAsync(storage.HistoricalPath, from, to);
            if (manifest.Count == 0)
                return Results.Ok(new { loaded = 0, sources = Array.Empty<string>() });

            int totalRecords = 0;
            var loadedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in manifest)
            {
                var values = await ParquetStorage.ReadFileAsync(entry.FullPath, req.Tags, from, to);
                if (values.Count == 0) continue;

                var buf = bufMgr.GetOrCreateBuffer(entry.Source);
                var actualFrom = values.Min(v => v.Timestamp);
                var actualTo = values.Max(v => v.Timestamp);

                var chunk = new Chunk(entry.Source, actualFrom, actualTo, ChunkQuality.Loaded);
                chunk.AddRange(values);
                chunk.Seal();
                buf.InsertChunk(chunk);

                // Register source/tags in SourceManager if available
                if (srcMgr is not null)
                {
                    var ds = srcMgr.GetOrRegisterSource(entry.Source);
                    foreach (var tagName in entry.Tags)
                        ds.TryRegisterTag(new Tag { Name = tagName, Kind = entry.Kind });
                }

                totalRecords += values.Count;
                loadedSources.Add(entry.Source);
            }

            return Results.Ok(new
            {
                loaded = totalRecords,
                files = manifest.Count,
                sources = loadedSources.ToArray(),
                fromTs = from,
                toTs = to
            });
        });

        return app;
    }
}

public sealed record ArchiveLoadRequest(string? Source, string File, string[]? Tags = null);

public sealed record ArchiveLoadRangeRequest(string Source, string? Kind, string From, string To, string[]? Tags = null);

public sealed record ArchiveCompactRequest(string? Source, string? Kind, string From, string To);

public sealed record ArchiveScanRequest(string? From, string? To);

public sealed record ArchiveLoadHistoricalRequest(string From, string To, string[]? Tags = null);
