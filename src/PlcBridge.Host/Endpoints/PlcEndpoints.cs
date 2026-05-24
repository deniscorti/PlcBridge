namespace PlcBridge.Host.Endpoints;

public static class PlcEndpoints
{
    public static IEndpointRouteBuilder MapPlcEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/plcs", () => Results.Ok(Array.Empty<object>()));
        g.MapGet("/plcs/{id}", (string id) => Results.NotFound());
        g.MapGet("/plcs/{id}/tags", (string id) => Results.Ok(Array.Empty<object>()));
        g.MapGet("/plcs/{id}/tags/{name}", (string id, string name) => Results.NotFound());
        g.MapPost("/plcs/{id}/tags/{name}", (string id, string name) => Results.Accepted());

        return app;
    }
}
