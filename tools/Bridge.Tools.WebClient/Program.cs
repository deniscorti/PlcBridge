// ──────────────────────────────────────────────────────────
//  Bridge Web Client — Test dashboard
//  Serves a static HTML page that connects via WebSocket
//  and REST to a running Bridge instance.
//
//  Usage:
//    dotnet run -- [--bridge http://localhost:5080]
//
//  Then open http://localhost:5180 in a browser.
// ──────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var bridgeUrl = args.SkipWhile(a => a != "--bridge").Skip(1).FirstOrDefault() ?? "http://localhost:5080";

// Inject bridge URL into the static page
app.MapGet("/config", () => Results.Ok(new { bridgeUrl, wsUrl = bridgeUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws" }));

app.UseStaticFiles();
app.MapFallbackToFile("index.html");

Console.WriteLine($"Bridge Web Client");
Console.WriteLine($"  Dashboard: http://localhost:5180");
Console.WriteLine($"  Bridge:    {bridgeUrl}");

app.Run("http://localhost:5180");
