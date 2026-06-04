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
using Bridge.InterBridge.UdpSender;
using Bridge.InterBridge.WsClient;
using Bridge.Storage.Parquet;
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

    // ── Buffer ──
    BufferManager? bufMgr = null;
    if (bridgeOpts.Buffer.Enabled)
    {
        bufMgr = new BufferManager(
            bridgeOpts.Buffer.ChunkDurationMin,
            bridgeOpts.Buffer.InMemoryMinutes);
        builder.Services.AddSingleton(bufMgr);
    }

    // ── Storage (Parquet write) ──
    ParquetStorage? storage = null;
    if (bridgeOpts.Buffer.PersistToDisk)
    {
        storage = new ParquetStorage(bridgeOpts.Buffer.ParquetOutputPath, bridgeOpts.Buffer.ParquetArchivePath);
        builder.Services.AddSingleton(storage);
    }

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

    // ── Register direct DataSources (optional: DataService can also read directly from PLC) ──
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

    // ── Connect to upstream bridges (Sources[]) ──
    var wsClients = new List<BridgeWsClient>();
    foreach (var srcCfg in bridgeOpts.Sources.Where(s => s.Enabled))
    {
        var ds = srcMgr.GetOrRegisterSource(srcCfg.Id);
        var clientOpts = new BridgeWsClientOptions
        {
            Id = srcCfg.Id,
            Url = srcCfg.Url,
            ApiKey = srcCfg.ApiKey,
            AutoDiscovery = srcCfg.AutoDiscovery,
            Tags = srcCfg.Tags,
            DownsampleMs = srcCfg.ForwardIntervalMs,
            Compression = srcCfg.Compression,
            BatchMode = srcCfg.BatchMode,
            ChunkSync = srcCfg.ChunkSync,
            HeartbeatIntervalMs = bridgeOpts.HeartbeatIntervalMs,
            ChunkSyncMaxBandwidthKbps = srcCfg.ChunkSyncMaxBandwidthKbps,
            SelectiveSubscription = false
        };
        var client = new BridgeWsClient(clientOpts, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<BridgeWsClient>());

        client.OnValue += srcMgr.HandleValue;

        client.OnTagsDiscovered += tags =>
        {
            foreach (var (sourceId, tagName, kind) in tags)
            {
                var tagDs = srcMgr.GetOrRegisterSource(sourceId);
                var tag = new Tag { Name = tagName, Kind = kind };
                if (tagDs.TryRegisterTag(tag))
                    Log.Debug("Discovered tag '{Tag}' ({Kind}) in source '{Source}' from upstream", tagName, kind, sourceId);
            }
            Log.Information("Discovered {Count} tags from upstream bridge '{ClientId}'", tags.Count, srcCfg.Id);
        };

        srcMgr.RegisterWsClient(srcCfg.Id, client);
        wsClients.Add(client);
    }

    // ── UDP inter-bridge sender (DataService → DataServer) ──
    var udpSenders = new List<UdpBridgeSender>();
    foreach (var dest in bridgeOpts.UdpDestinations.Where(d => d.Enabled))
    {
        foreach (var sourceId in dest.Sources)
        {
            var sender = new UdpBridgeSender(
                dest.Id, dest.Host, dest.Port, sourceId,
                dest.MaxPacketBytes, dest.Enabled,
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<UdpBridgeSender>());

            // Start periodic mapping broadcast so the receiver can auto-discover source/tag names
            var ds = srcMgr.GetOrRegisterSource(sourceId);
            sender.StartMappingBroadcast(() => ds.Tags);

            udpSenders.Add(sender);
        }
    }

    // ── Notify WS clients when sources/tags change (e.g. auto-discovery from upstream or UDP) ──
    Timer? sourceChangedDebounce = null;
    srcMgr.OnSourceChanged += sourceId =>
    {
        sourceChangedDebounce?.Dispose();
        sourceChangedDebounce = new Timer(state =>
            _ = connMgr.BroadcastAsync(new { type = "sourcesChanged" }),
            null, 500, Timeout.Infinite);
    };

    // ── Wire up value dispatch ──
    srcMgr.OnValue += value =>
    {
        // Feed buffer
        bufMgr?.Add(value);

        // Push to WS subscribers
        var subscribers = broker.GetSubscribers(value);
        if (subscribers.Count > 0)
        {
            List<string>? verboseIds = null;
            foreach (var connId in subscribers)
            {
                if (batchAccumulator.TryAccumulate(connId, value.Source, value.Tag, value.Value, value.Timestamp, value.MsgId))
                    continue;
                verboseIds ??= [];
                verboseIds.Add(connId);
            }
            if (verboseIds is { Count: > 0 })
            {
                object pushMsg = new { type = value.Kind.ToString().ToLowerInvariant(), source = value.Source, tag = value.Tag, value = value.Value, ts = value.Timestamp, msgId = value.MsgId };
                _ = connMgr.SendToManyAsync(verboseIds, pushMsg);
            }
        }

        // Forward to UDP senders
        foreach (var sender in udpSenders)
            _ = sender.SendAsync(value);
    };

    // ── Parquet flush + chunkReady on chunk sealed ──
    if (bufMgr is not null)
    {
        bufMgr.OnChunkSealed += chunk =>
        {
            _ = Task.Run(async () =>
            {
                List<string>? parquetFiles = null;

                // Flush to Parquet first (so files are ready for download)
                if (storage is not null)
                {
                    try
                    {
                        parquetFiles = await storage.WriteChunkAsync(chunk);
                        Log.Information("Flushed chunk {ChunkId} for source {Source} ({Records} records, {FileCount} files)",
                            chunk.ChunkId, chunk.Source, chunk.RecordCount, parquetFiles.Count);
                    }
                    catch (Exception ex) { Log.Error(ex, "Failed to flush chunk {ChunkId}", chunk.ChunkId); }
                }

                // Notify all connected WS clients — include file names so DataServer can download via HTTP
                var chunkReadyMsg = new
                {
                    type = "chunkReady",
                    source = chunk.Source,
                    chunkId = chunk.ChunkId,
                    fromTs = chunk.FromTs,
                    toTs = chunk.ToTs,
                    firstMsgId = chunk.FirstMsgId,
                    lastMsgId = chunk.LastMsgId,
                    records = chunk.RecordCount,
                    files = parquetFiles ?? []
                };
                await connMgr.BroadcastAsync(chunkReadyMsg);
            });
        };
    }

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

    if (bufMgr is not null)
    {
        app.MapQueryEndpoints();
        app.MapArchiveEndpoints();
    }

    Log.Information("Bridge DataService starting on port {Port}", bridgeOpts.Http.Port);

    // ── Start everything ──
    await srcMgr.StartAllAsync();

    foreach (var client in wsClients)
        _ = client.ConnectAsync();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Bridge DataService terminated unexpectedly");
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
