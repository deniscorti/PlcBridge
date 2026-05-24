using System.Net.WebSockets;

namespace PlcBridge.Host.WebSockets;

public static class PlcWebSocket
{
    public static IEndpointRouteBuilder MapPlcWebSocket(this IEndpointRouteBuilder app, string path)
    {
        app.Map(path, async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            // TODO: handshake auth, registrazione su SubscriptionBroker, loop receive/send.
            await Task.CompletedTask;
        });
        return app;
    }
}
