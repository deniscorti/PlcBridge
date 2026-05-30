using Bridge.Contracts.Rest;
using Bridge.Core.Services;
using Bridge.Host.Configuration;
using Microsoft.Extensions.Options;

namespace Bridge.Host.Endpoints;

public static class HealthEndpoints
{
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", (IOptions<BridgeOptions> opts) =>
        {
            var uptime = (long)(DateTimeOffset.UtcNow - StartTime).TotalSeconds;
            return Results.Ok(new HealthDto("ok", opts.Value.Mode.ToString(), uptime));
        });

        app.MapGet("/api/status", (IOptions<BridgeOptions> opts, SourceManager mgr) =>
        {
            var uptime = (long)(DateTimeOffset.UtcNow - StartTime).TotalSeconds;
            var sources = mgr.GetAllSources().Select(s =>
            {
                var input = mgr.GetInputForSource(s.Id);
                return new SourceStatusDto(s.Id, input?.IsConnected ?? false, s.Tags.Count);
            }).ToList();
            return Results.Ok(new StatusDto(opts.Value.Mode.ToString(), uptime, sources));
        });

        return app;
    }
}
