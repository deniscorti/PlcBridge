// ──────────────────────────────────────────────────────────
//  Bridge Web Client — Test dashboard
//  Serves a static HTML page that connects via WebSocket
//  and REST to a running Bridge instance.
//
//  Includes a REST proxy to avoid CORS issues: the browser
//  calls /proxy?url=http://bridge:5080/api/... and this
//  server forwards the request to the bridge.
//
//  Usage:
//    dotnet run -- [--bridge http://localhost:5080]
//
//  Then open http://localhost:5180 in a browser.
// ──────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("proxy", c => c.Timeout = TimeSpan.FromMinutes(5));
var app = builder.Build();

var bridgeUrl = args.SkipWhile(a => a != "--bridge").Skip(1).FirstOrDefault() ?? "http://localhost:5080";

// Inject bridge URL into the static page
app.MapGet("/config", () => Results.Ok(new { bridgeUrl, wsUrl = bridgeUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws" }));

// REST proxy — forwards GET/POST to any bridge URL, avoids CORS
app.Map("/proxy", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
{
    var targetUrl = ctx.Request.Query["url"].ToString();
    if (string.IsNullOrEmpty(targetUrl))
        return Results.BadRequest(new { error = "Missing 'url' query parameter" });

    // Only allow proxying to localhost/private IPs for safety
    var uri = new Uri(targetUrl);
    var client = httpFactory.CreateClient("proxy");

    try
    {
        HttpResponseMessage response;
        if (ctx.Request.Method == "POST")
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            response = await client.PostAsync(targetUrl, content);
        }
        else
        {
            response = await client.GetAsync(targetUrl);
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        var responseBody = await response.Content.ReadAsStringAsync();

        return Results.Content(responseBody, contentType, statusCode: (int)response.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
});

app.UseStaticFiles();
app.MapFallbackToFile("index.html");

Console.WriteLine($"Bridge Web Client");
Console.WriteLine($"  Dashboard: http://localhost:5180");
Console.WriteLine($"  Bridge:    {bridgeUrl}");

app.Run("http://localhost:5180");
