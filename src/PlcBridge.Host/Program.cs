using PlcBridge.Host.Endpoints;
using PlcBridge.Host.WebSockets;

var builder = WebApplication.CreateBuilder(args);

// TODO: Serilog, OpenTelemetry, options binding, auth, DI dei servizi PlcBridge.Core / .Plc
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseWebSockets();
app.MapHealthChecks("/health");
app.MapPlcEndpoints();
app.MapPlcWebSocket("/ws");

app.Run();
