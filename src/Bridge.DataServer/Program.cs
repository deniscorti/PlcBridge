using Bridge.Core.Abstractions;
using Bridge.Core.Buffer;
using Bridge.Core.Model;
using Bridge.Core.Services;
using Bridge.Host.Auth;
using Bridge.Host.Configuration;
using Bridge.Host.Endpoints;
using Bridge.Host.Middleware;
using Bridge.Host.WebSockets;
using Bridge.InterBridge.ChunkTransfer;
using Bridge.InterBridge.UdpReceiver;
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

    // ── Buffer (always on for DataServer) ──
    var bufMgr = new BufferManager(
        bridgeOpts.Buffer.ChunkDurationMin,
        bridgeOpts.Buffer.InMemoryMinutes);
    builder.Services.AddSingleton(bufMgr);

    // ── Storage (Parquet: salva chunk ricevuti come archivio + carica archivi per analisi offline) ──
    var storage = new ParquetStorage(bridgeOpts.Buffer.ParquetOutputPath, bridgeOpts.Buffer.ParquetArchivePath);
    builder.Services.AddSingleton(storage);

    // ── Chunk transfer (downloads Parquet files via HTTP, saves to archive) ──
    builder.Services.AddSingleton(sp => new ChunkTransferService(
        sp.GetRequiredService<BufferManager>(),
        bridgeOpts.Buffer.ParquetArchivePath,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<ChunkTransferService>()));

    var app = builder.Build();

    // ── Initialize ──
    var srcMgr = app.Services.GetRequiredService<SourceManager>();
    var broker = app.Services.GetRequiredService<ISubscriptionBroker>();
    var connMgr = app.Services.GetRequiredService<WsConnectionManager>();
    var layoutMgr = app.Services.GetRequiredService<CompactLayoutManager>();
    var batchAccumulator = new CompactBatchAccumulator(layoutMgr, connMgr, bridgeOpts.WebSocket.CompactFlushMs);
    var alarmService = app.Services.GetRequiredService<AlarmService>();
    var chunkTransfer = app.Services.GetRequiredService<ChunkTransferService>();

    srcMgr.SetAlarmService(alarmService);

    // ── Chunk transfer: file Parquet scaricati e salvati dal ChunkTransferService.
    //    Qui li carichiamo nel buffer in memoria per renderli disponibili ai client. ──
    chunkTransfer.OnChunkFilesReceived += (localPaths, info) =>
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var allValues = new List<TagValue>();
                foreach (var localPath in localPaths)
                {
                    var values = await ParquetStorage.ReadFileAsync(localPath);
                    allValues.AddRange(values);
                    Log.Debug("Read {Count} records from {Path}", values.Count, Path.GetFileName(localPath));
                }

                if (allValues.Count == 0) return;

                var buf2 = bufMgr.GetOrCreateBuffer(info.SourceId);
                var chunk = new Chunk(info.SourceId, info.FromTs, info.ToTs, ChunkQuality.Full);
                chunk.AddRange(allValues);
                chunk.Seal();
                buf2.ReplaceChunk(chunk);

                Log.Information("Chunk {ChunkId} loaded into buffer ({Records} records from {FileCount} files)",
                    info.ChunkId, allValues.Count, localPaths.Length);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load chunk files for {ChunkId}", info.ChunkId);
            }
        });
    };

    // ── Selective subscription aggregator (created before WS clients so discovery can register) ──
    UpstreamSubscriptionAggregator? subAggregator = null;
    if (bridgeOpts.SelectiveSubscription)
    {
        subAggregator = new UpstreamSubscriptionAggregator(
            broker, srcMgr, bufMgr,
            bridgeOpts.BackfillMinutes,
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<UpstreamSubscriptionAggregator>());
        Log.Information("Selective subscription enabled with {Backfill}min backfill", bridgeOpts.BackfillMinutes);
    }

    // ── UDP inter-bridge receiver (created early so WS discovery can register sources) ──
    UdpBridgeReceiver? udpReceiver = null;
    if (bridgeOpts.UdpReceiver is { Enabled: true })
    {
        udpReceiver = new UdpBridgeReceiver(
            bridgeOpts.UdpReceiver.ListenPort,
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<UdpBridgeReceiver>());

        if (bridgeOpts.UdpReceiver.AutoDiscovery)
            udpReceiver.EnableAutoDiscovery(srcMgr);

        udpReceiver.OnValue += srcMgr.HandleValue;
    }

    // ── Connect to upstream bridges (Sources[]) ──
    var wsClients = new List<BridgeWsClient>();
    foreach (var srcCfg in bridgeOpts.Sources.Where(s => s.Enabled))
    {
        var ds = srcMgr.GetOrRegisterSource(srcCfg.Id);
        var useSelective = bridgeOpts.SelectiveSubscription;
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
            SelectiveSubscription = useSelective
        };
        var client = new BridgeWsClient(clientOpts, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<BridgeWsClient>());

        client.OnValue += srcMgr.HandleValue;

        client.OnTagsDiscovered += tags =>
        {
            var discoveredSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sourceId, tagName, kind) in tags)
            {
                var tagDs = srcMgr.GetOrRegisterSource(sourceId);
                var tag = new Tag { Name = tagName, Kind = kind };
                if (tagDs.TryRegisterTag(tag))
                    Log.Debug("Discovered tag '{Tag}' ({Kind}) in source '{Source}' from upstream", tagName, kind, sourceId);
                discoveredSources.Add(sourceId);
            }

            // Register this WS client for all discovered sources in the aggregator
            // so that downstream subscribe("Line1", ...) gets forwarded upstream
            if (subAggregator is not null)
            {
                foreach (var sid in discoveredSources)
                    subAggregator.RegisterClient(sid, client);
            }

            // Also register WS client in SourceManager for command relay
            foreach (var sid in discoveredSources)
                srcMgr.RegisterWsClient(sid, client);

            Log.Information("Discovered {Count} tags in {Sources} source(s) from upstream bridge '{ClientId}'",
                tags.Count, discoveredSources.Count, srcCfg.Id);
        };

        // Chunk transfer: receive chunkReady from upstream DataService
        client.OnChunkReady += chunkInfo =>
            chunkTransfer.OnChunkReady(srcCfg.Id, client, chunkInfo);

        srcMgr.RegisterWsClient(srcCfg.Id, client);
        wsClients.Add(client);
    }

    // Register initial WS clients in aggregator (by config Id)
    // Real source names (Line1, Line2...) will be added dynamically via OnTagsDiscovered
    if (subAggregator is not null)
    {
        foreach (var client in wsClients)
            subAggregator.RegisterClient(client.SourceId, client);
    }


    // ── Wire up value dispatch ──
    srcMgr.OnValue += value =>
    {
        // Feed buffer
        bufMgr.Add(value);

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
    };

    // ── chunkReady notification on chunk sealed ──
    bufMgr.OnChunkSealed += chunk =>
    {
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
    app.MapQueryEndpoints();
    app.MapArchiveEndpoints();

    Log.Information("Bridge DataServer starting on port {Port}", bridgeOpts.Http.Port);

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
    Log.Fatal(ex, "Bridge DataServer terminated unexpectedly");
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
