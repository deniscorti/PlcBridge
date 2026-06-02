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
using Bridge.InterBridge.ChunkTransfer;
using Bridge.InterBridge.UdpReceiver;
using Bridge.InterBridge.UdpSender;
using Bridge.InterBridge.WsClient;
using Bridge.Storage.Parquet;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    // Support --config <file> to load a custom configuration file per instance
    var configFile = GetConfigFileFromArgs(args);

    Console.WriteLine($"Starting Bridge Host... config {configFile}");

    var builder = WebApplication.CreateBuilder(args);


    if (string.IsNullOrEmpty(configFile))
    {
        var env = builder.Environment.EnvironmentName;
        configFile = $"appsettings_{env}.json";
    }


    if (configFile is not null)
    {
        builder.Configuration.AddJsonFile(configFile, optional: false, reloadOnChange: false);
        Log.Information("Loaded configuration from {ConfigFile}", configFile);
    }

    builder.Host.UseSerilog((ctx, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console());

    // ── Configuration ──
    var bridgeSection = builder.Configuration.GetSection(BridgeOptions.SectionName);
    builder.Services.Configure<BridgeOptions>(bridgeSection);
    var bridgeOpts = bridgeSection.Get<BridgeOptions>() ?? new BridgeOptions();

    // ── HTTP: bind Kestrel alla porta configurata ──
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

    // Rate limiter
    builder.Services.AddSingleton(new RateLimiter(
        bridgeOpts.WebSocket.MaxQueriesPerMinute,
        bridgeOpts.WebSocket.MaxSubscriptionsPerClient,
        bridgeOpts.WebSocket.MaxMessageQueueSize));

    // ── Buffer (DataService/DataServer) ──
    var needsBuffer = bridgeOpts.Mode is BridgeMode.DataService or BridgeMode.DataServer;
    if (needsBuffer)
    {
        builder.Services.AddSingleton(new BufferManager(
            bridgeOpts.Buffer.ChunkDurationMin,
            bridgeOpts.Buffer.InMemoryMinutes));
    }

    // ── Storage ──
    // DataService: scrive in ParquetOutputPath
    // DataServer: carica da ParquetArchivePath (non scrive)
    var needsStorage = (bridgeOpts.Mode == BridgeMode.DataService && bridgeOpts.Buffer.PersistToDisk)
                    || bridgeOpts.Mode == BridgeMode.DataServer;
    if (needsStorage)
    {
        var outputPath = bridgeOpts.Buffer.ParquetOutputPath;
        var archivePath = bridgeOpts.Buffer.ParquetArchivePath;
        builder.Services.AddSingleton(new ParquetStorage(outputPath, archivePath));
    }

    // ── Chunk transfer (DataServer) ──
    if (bridgeOpts.Mode == BridgeMode.DataServer)
    {
        builder.Services.AddSingleton<ChunkTransferService>();
    }

    var app = builder.Build();

    // ── Initialize services ──
    var srcMgr = app.Services.GetRequiredService<SourceManager>();
    var broker = app.Services.GetRequiredService<ISubscriptionBroker>();
    var connMgr = app.Services.GetRequiredService<WsConnectionManager>();
    var layoutMgr = app.Services.GetRequiredService<CompactLayoutManager>();
    var batchAccumulator = new CompactBatchAccumulator(layoutMgr, connMgr, bridgeOpts.WebSocket.CompactFlushMs);
    var alarmService = app.Services.GetRequiredService<AlarmService>();
    var bufMgr = app.Services.GetService<BufferManager>();
    var storage = app.Services.GetService<ParquetStorage>();
    var chunkTransfer = app.Services.GetService<ChunkTransferService>();

    srcMgr.SetAlarmService(alarmService);

    // ── Input registry (extensible: add new protocols by registering factories) ──
    var inputRegistry = new InputRegistry();
    inputRegistry.Register(new MockInputFactory());
    inputRegistry.Register(new UdpInputFactory());
    // Future: inputRegistry.Register(new ModbusInputFactory());
    // Future: inputRegistry.Register(new AdsInputFactory());

    // ── Register direct DataSources (DataProvider / DataService with direct inputs) ──
    foreach (var dsCfg in bridgeOpts.DataSources)
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

    // ── Connect to upstream bridges (DataService/DataServer Sources[]) ──
    var wsClients = new List<BridgeWsClient>();
    foreach (var srcCfg in bridgeOpts.Sources)
    {
        var ds = srcMgr.GetOrRegisterSource(srcCfg.DataSource);
        var useSelective = bridgeOpts.SelectiveSubscription && bridgeOpts.Mode == BridgeMode.DataServer;
        var clientOpts = new BridgeWsClientOptions
        {
            Id = srcCfg.Id,
            DataSource = srcCfg.DataSource,
            Url = srcCfg.Url,
            ApiKey = srcCfg.ApiKey,
            SubscribeTags = srcCfg.SubscribeTags,
            DownsampleMs = srcCfg.ForwardIntervalMs,
            Compression = srcCfg.Compression,
            BatchMode = srcCfg.BatchMode,
            ChunkSync = srcCfg.ChunkSync,
            HeartbeatIntervalMs = bridgeOpts.HeartbeatIntervalMs,
            ChunkSyncMaxBandwidthKbps = srcCfg.ChunkSyncMaxBandwidthKbps,
            SelectiveSubscription = useSelective
        };
        var client = new BridgeWsClient(clientOpts, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<BridgeWsClient>());

        client.OnValue += srcMgr.HandleValue;

        // Auto-discover upstream tags on connect
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

        // Chunk transfer (DataServer receives chunkReady from DataService)
        if (chunkTransfer is not null)
        {
            client.OnChunkReady += chunkInfo =>
                chunkTransfer.OnChunkReady(srcCfg.DataSource, client, chunkInfo);
        }

        srcMgr.RegisterWsClient(srcCfg.DataSource, client);
        wsClients.Add(client);
    }

    // ── Selective subscription aggregator (DataServer) ──
    UpstreamSubscriptionAggregator? subAggregator = null;
    if (bridgeOpts.Mode == BridgeMode.DataServer && bridgeOpts.SelectiveSubscription)
    {
        subAggregator = new UpstreamSubscriptionAggregator(
            broker, srcMgr, bufMgr,
            bridgeOpts.BackfillMinutes,
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<UpstreamSubscriptionAggregator>());

        foreach (var client in wsClients)
            subAggregator.RegisterClient(client.SourceId, client);

        Log.Information("Selective subscription enabled with {Backfill}min backfill", bridgeOpts.BackfillMinutes);
    }

    // ── UDP inter-bridge sender (DataService → DataServer) ──
    var udpSenders = new List<UdpBridgeSender>();
    foreach (var dest in bridgeOpts.UdpDestinations)
    {
        foreach (var sourceId in dest.Sources)
        {
            var sender = new UdpBridgeSender(
                dest.Id, dest.Host, dest.Port, sourceId,
                dest.MaxPacketBytes, dest.Enabled,
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<UdpBridgeSender>());
            udpSenders.Add(sender);
        }
    }

    // ── UDP inter-bridge receiver (DataServer receives from DataService) ──
    UdpBridgeReceiver? udpReceiver = null;
    if (bridgeOpts.UdpReceiver is not null)
    {
        udpReceiver = new UdpBridgeReceiver(
            bridgeOpts.UdpReceiver.ListenPort,
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<UdpBridgeReceiver>());

        foreach (var rcvSrc in bridgeOpts.UdpReceiver.Sources)
        {
            var ds = srcMgr.GetOrRegisterSource(rcvSrc.DataSource);
            udpReceiver.RegisterSource(rcvSrc.DataSource, ds.Tags, rcvSrc.Enabled);
        }

        udpReceiver.OnValue += srcMgr.HandleValue;
    }

    // ── Wire up value dispatch ──
    srcMgr.OnValue += value =>
    {
        // Feed buffer
        bufMgr?.Add(value);

        // Push to WS subscribers (compact batch vs verbose)
        var subscribers = broker.GetSubscribers(value);
        if (subscribers.Count > 0)
        {
            List<string>? verboseIds = null;

            foreach (var connId in subscribers)
            {
                if (batchAccumulator.TryAccumulate(connId, value.Source, value.Tag, value.Value, value.Timestamp, value.MsgId))
                    continue; // accumulated into compact batch, will flush on timer

                verboseIds ??= [];
                verboseIds.Add(connId);
            }

            if (verboseIds is { Count: > 0 })
            {
                object pushMsg = value.Kind switch
                {
                    DataKind.Telemetry => new { type = "telemetry", source = value.Source, tag = value.Tag, value = value.Value, ts = value.Timestamp, msgId = value.MsgId },
                    DataKind.Event => new { type = "event", source = value.Source, tag = value.Tag, value = value.Value, ts = value.Timestamp, msgId = value.MsgId },
                    DataKind.Alarm => new { type = "alarm", source = value.Source, tag = value.Tag, value = value.Value, ts = value.Timestamp, msgId = value.MsgId },
                    _ => new { type = "unknown", source = value.Source, tag = value.Tag, value = value.Value, ts = value.Timestamp, msgId = value.MsgId }
                };
                _ = connMgr.SendToManyAsync(verboseIds, pushMsg);
            }
        }

        // Forward to UDP inter-bridge senders (DataService only)
        foreach (var sender in udpSenders)
        {
            _ = sender.SendAsync(value);
        }
    };

    // ── Parquet flush + chunkReady notification on chunk sealed ──
    if (bufMgr is not null)
    {
        bufMgr.OnChunkSealed += chunk =>
        {
            // Flush to Parquet (DataService with PersistToDisk, or DataServer)
            if (storage is not null && bridgeOpts.Buffer.PersistToDisk)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await storage.WriteChunkAsync(chunk);
                        Log.Information("Flushed chunk {ChunkId} for source {Source} ({Records} records)",
                            chunk.ChunkId, chunk.Source, chunk.RecordCount);
                    }
                    catch (Exception ex) { Log.Error(ex, "Failed to flush chunk {ChunkId}", chunk.ChunkId); }
                });
            }

            // Notify all connected WS clients that a chunk is ready (inter-bridge protocol)
            var chunkReadyMsg = new
            {
                type = "chunkReady",
                source = chunk.Source,
                chunkId = chunk.ChunkId,
                fromTs = chunk.FromTs,
                toTs = chunk.ToTs,
                firstMsgId = chunk.FirstMsgId,
                lastMsgId = chunk.LastMsgId,
                records = chunk.RecordCount
            };
            _ = connMgr.BroadcastAsync(chunkReadyMsg);
        };
    }

    // Note: il DataServer NON scrive Parquet. I chunk Full ricevuti via chunk transfer
    // rimangono in memoria. Per persistenza, il DataService produce i file Parquet
    // che possono essere trasferiti al DataServer e caricati via /archives/load.

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

    if (needsBuffer)
    {
        app.MapQueryEndpoints();
        app.MapArchiveEndpoints();
    }

    Log.Information("Bridge starting in {Mode} mode on port {Port}", bridgeOpts.Mode, bridgeOpts.Http.Port);

    // ── Start everything ──
    await srcMgr.StartAllAsync();

    foreach (var client in wsClients)
        _ = client.ConnectAsync();

    if (udpReceiver is not null)
        await udpReceiver.StartAsync();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Bridge terminated unexpectedly");
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
