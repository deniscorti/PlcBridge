using Bridge.Contracts.Rest;
using Bridge.Core.Model;
using Bridge.Core.Services;

namespace Bridge.Host.Endpoints;

public static class SourceEndpoints
{
    public static IEndpointRouteBuilder MapSourceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/sources", (SourceManager mgr) =>
        {
            var sources = mgr.GetAllSources().Select(s => new SourceDto(
                s.Id,
                s.Tags.Values.Select(t => new TagDto(t.Name, t.Kind.ToString(), t.Address, t.PollMs)).ToList()
            )).ToList();
            return Results.Ok(sources);
        });

        g.MapGet("/sources/{source}/tags", (string source, SourceManager mgr) =>
        {
            var ds = mgr.GetSource(source);
            if (ds is null) return Results.NotFound(new { error = "SOURCE_NOT_FOUND", message = $"Source '{source}' not found." });
            var tags = ds.Tags.Values.Select(t => new TagDto(t.Name, t.Kind.ToString(), t.Address, t.PollMs)).ToList();
            return Results.Ok(tags);
        });

        g.MapGet("/sources/{source}/tags/{name}", async (string source, string name, SourceManager mgr) =>
        {
            var ds = mgr.GetSource(source);
            if (ds is null) return Results.NotFound(new { error = "SOURCE_NOT_FOUND" });

            var tag = ds.GetTag(name);
            if (tag is null) return Results.NotFound(new { error = "TAG_NOT_FOUND", message = $"Tag '{name}' not found in source '{source}'." });

            // Try last value from cache first
            var last = ds.GetLastValue(name);
            if (last is not null)
            {
                return Results.Ok(new TagValueDto(last.Source, last.Tag, last.Value, last.Timestamp, last.MsgId));
            }

            // DataProvider: read directly from input
            var input = mgr.GetInputForSource(source);
            if (input is not null)
            {
                var value = await input.ReadTagAsync(name);
                return Results.Ok(new TagValueDto(source, name, value, DateTimeOffset.UtcNow, 0));
            }

            return Results.NotFound(new { error = "NO_VALUE", message = "No value available yet." });
        });

        g.MapPost("/sources/{source}/tags/{name}", async (string source, string name, WriteRequest req, SourceManager mgr) =>
        {
            var ds = mgr.GetSource(source);
            if (ds is null) return Results.NotFound(new { error = "SOURCE_NOT_FOUND" });

            var tag = ds.GetTag(name);
            if (tag is null) return Results.NotFound(new { error = "TAG_NOT_FOUND" });

            var input = mgr.GetInputForSource(source);
            if (input is null) return Results.BadRequest(new { error = "NO_INPUT", message = "No writable input for this source." });

            await input.WriteTagAsync(name, req.Value);
            return Results.Accepted();
        });

        return app;
    }
}
