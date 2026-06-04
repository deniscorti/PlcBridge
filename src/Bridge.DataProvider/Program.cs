using Bridge.Core.Abstractions;
using Bridge.Core.Model;
using Bridge.Core.Services;
using Bridge.Host.Auth;
using Bridge.Host.Configuration;
using Bridge.Host.Endpoints;
using Bridge.Host.Middleware;
using Bridge.Host.WebSockets;
using Bridge.Inputs;
using Bridge.Inputs.Mock;
using Bridge.Inputs.Udp;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Support --config <file> override
    var configFile = GetConfigFileFromArgs(args);
    if (!string.IsNullOrEmpty(configFile))
    {
        builder.Configuration.AddJsonFile(configFile, optional: false, reloadOnChange: false);
        Log.Information("Loaded explicit config: {ConfigFile}", configFile);
    }

    builder.Host.UseSerilog((ctx, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console());

    // ── Configuration ──
    var bridgeSection = builder.Configuration.GetSection(BridgeOptions.SectionName);
    builder.Services.Configure<BridgeOptions>(bridgeSection);
    var bridgeOpts = bridgeSection.Get<BridgeOptions>() ?? new BridgeOptions();

    // ── HTTP: bind Kestrel ──
    builder.WebHost.UseUrls($"http://*:{bridgeOpts.Http.Port}");

    // ── Auth ──
    if (!string.IsNullOrEmpty(bridgeOpts.Auth.ApiKey))
    {
        builder.Services.AddAuthentication(ApiKeyAuthHandler.SchemeName)
            .AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, opts =>
            {
                opts.ApiKey = bridgeOpts.Auth.ApiKey;
            });
        builder.Services.AddAuthorization();
    }

    // ── Core services ──
    builder.Services.AddSingleton<ISubscriptionBroker, SubscriptionBroker>();
    builder.Services.AddSingleton<SourceManager>();
    builder.Services.AddSingleton<WsConnectionManager>();
    builder.Services.AddSingleton<CompactLayoutManager>();
    builder.Services.AddSingleton<AlarmService>();

    builder.Services.AddSingleton(new RateLimiter(
        bridgeOpts.WebSocket.MaxQueriesPerMinute,
        bridgeOpts.WebSocket.MaxSubscriptionsPerClient,
        bridgeOpts.WebSocket.MaxMessageQueueSize));

    var app = builder.Build();

    // ── Initialize ──
    var srcMgr = app.Services.GetRequiredService<SourceManager>();
    var broker = app.Services.GetRequiredService<ISubscriptionBroker>();
    var connMgr = app.Services.GetRequiredService<WsConnectionManager>();
    var layoutMgr = app.Services.GetRequiredService<CompactLayoutManager>();
    var batchAccumulator = new CompactBatchAccumulator(layoutMgr, connMgr, bridgeOpts.WebSocket.CompactFlushMs);
    var alarmService = app.Services.GetRequiredService<AlarmService>();

    srcMgr.SetAlarmService(alarmService);

    // ── Input registry ──
    var inputRegistry = new InputRegistry();
    inputRegistry.Register(new MockInputFactory());
    inputRegistry.Register(new UdpInputFactory());

    // ── Register DataSources (direct inputs from PLC) ──
    foreach (var dsCfg in bridgeOpts.DataSources.Where(ds => ds.Enabled))
    {
        var ds = srcMgr.GetOrRegisterSource(dsCfg.Id);
        foreach (var inputCfg in dsCfg.Inputs)
        {
            foreach (var tagCfg in inputCfg.Tags)
            {
                var kind = Enum.Parse<DataKind>(tagCfg.DataKind, true);
                ds.RegisterTag(new Tag { Name = tagCfg.Name, Kind = kind, Address = tagCfg.Address, PollMs = tagCfg.PollMs });
            }

            IDataInputConfig inputConfig = inputCfg.Type.ToLowerInvariant() switch
            {
                "udp" => new UdpInputConfig
                {
                    ListenPort = inputCfg.ListenPort ?? 9100,
                    Protocol = inputCfg.Protocol ?? "custom-v1",
                    AutoDiscovery = inputCfg.AutoDiscovery
                },
                _ => new GenericInputConfig(inputCfg.Type)
            };

            var input = inputRegistry.Create(inputCfg.Type, ds, inputConfig);

            if (input is UdpInput udpInput && inputCfg.AutoDiscovery)
            {
                udpInput.SetSourceResolver(id => srcMgr.GetOrRegisterSource(id));
                udpInput.OnSourceDiscovered += discoveredDs =>
                {
                    Log.Information("Auto-discovered DataSource '{SourceId}' with {TagCount} tags",
                        discoveredDs.Id, discoveredDs.Tags.Count);
                };
            }

            srcMgr.AddInput(input);
        }
    }

    // ── Notify WS clients when sources/tags change (e.g. auto-discovery) ──
    Timer? sourceChangedDebounce = null;
    srcMgr.OnSourceChanged += sourceId =>
    {
        sourceChangedDebounce?.Dispose();
        sourceChangedDebounce = new Timer(state =>
            _ = connMgr.BroadcastAsync(new { type = "sourcesChanged" }),
            null, 500, Timeout.Infinite);
    };

    // ── Wire up value dispatch (push to WS subscribers) ──
    srcMgr.OnValue += value =>
    {
        var subscribers = broker.GetSubscribers(value);
        if (subscribers.Count > 0)
        {
            List<string>? verboseIds = null;
            foreach (var connId2 in subscribers)
            {
                if (batchAccumulator.TryAccumulate(connId2, value.Source, value.Tag, value.Value, value.Timestamp, value.MsgId))
                    continue;
                verboseIds ??= [];
                verboseIds.Add(connId2);
            }
            if (verboseIds is { Count: > 0 })
            {
                object pushMsg = new { type = value.Kind.ToString().ToLowerInvariant(), source = value.Source, tag = value.Tag, value = value.Value, ts = value.Timestamp, msgId = value.MsgId };
                _ = connMgr.SendToManyAsync(verboseIds, pushMsg);
            }
        }
    };

    // ── Map endpoints ──
    app.UseWebSockets();

    if (!string.IsNullOrEmpty(bridgeOpts.Auth.ApiKey))
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }

    app.MapHealthEndpoints();
    app.MapSourceEndpoints();
    app.MapMetricsEndpoints();
    app.MapBridgeWebSocket(bridgeOpts.WebSocket.Path);

    Log.Information("Bridge DataProvider starting on port {Port}", bridgeOpts.Http.Port);

    await srcMgr.StartAllAsync();
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Bridge DataProvider terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

static string? GetConfigFileFromArgs(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--config" or "-c")
            return args[i + 1];
    }
    return null;
}
