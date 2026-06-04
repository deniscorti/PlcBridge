using Bridge.Core.Model;

namespace Bridge.Host.Configuration;

public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    public HttpOptions Http { get; set; } = new();
    public WebSocketOptions WebSocket { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public BufferOptions Buffer { get; set; } = new();
    public int CommandTimeoutMs { get; set; } = 5000;
    public int HeartbeatIntervalMs { get; set; } = 10000;
    public int HeartbeatMaxMissed { get; set; } = 3;
    public List<DataSourceOptions> DataSources { get; set; } = [];
    public List<SourceConnectionOptions> Sources { get; set; } = [];
    public bool SelectiveSubscription { get; set; } = true;
    public int BackfillMinutes { get; set; } = 10;
    public List<UdpDestinationOptions> UdpDestinations { get; set; } = [];
    public UdpReceiverOptions? UdpReceiver { get; set; }

    // Kept for backward compatibility with old configs that set "Mode"
    public string? Mode { get; set; }
}

public sealed class HttpOptions
{
    public int Port { get; set; } = 5080;
}

public sealed class WebSocketOptions
{
    public string Path { get; set; } = "/ws";
    public int MaxConnections { get; set; } = 100;
    public int MaxSubscriptionsPerClient { get; set; } = 200;
    public int MaxQueriesPerMinute { get; set; } = 60;
    public int MaxMessageQueueSize { get; set; } = 10000;

    /// <summary>Compact batch flush interval in milliseconds. Lower = less latency, higher = more batching.</summary>
    public int CompactFlushMs { get; set; } = 50;
}

public sealed class AuthOptions
{
    public string? ApiKey { get; set; }
    public string? JwtSecret { get; set; }
}

public sealed class DataSourceOptions
{
    public bool Enabled { get; set; } = true;
    public required string Id { get; set; }
    public List<InputOptions> Inputs { get; set; } = [];
}

public sealed class InputOptions
{
    public required string Type { get; set; } // "Ads", "Udp", "Mock"
    public string? Host { get; set; }
    public string? AmsNetId { get; set; }
    public int? Port { get; set; }
    public int? ListenPort { get; set; }
    public string? Protocol { get; set; }
    public bool AutoDiscovery { get; set; }
    public List<TagOptions> Tags { get; set; } = [];
}

public sealed class TagOptions
{
    public required string Name { get; set; }
    public string? Address { get; set; }
    public string DataKind { get; set; } = "Telemetry";
    public int? PollMs { get; set; }
    public int? Offset { get; set; }
    public int? Length { get; set; }
}

public sealed class BufferOptions
{
    public bool Enabled { get; set; } = true;
    public int InMemoryMinutes { get; set; } = 60;
    public int ChunkDurationMin { get; set; } = 5;

    // ── DataService: scrittura Parquet ──

    /// <summary>Se true, il DataService scrive i chunk sealed su disco in formato Parquet.</summary>
    public bool PersistToDisk { get; set; } = true;

    /// <summary>Cartella dove il DataService scrive i file Parquet dei chunk sealed.</summary>
    public string ParquetOutputPath { get; set; } = "./data/parquet";

    // ── DataServer: archivio Parquet ──

    /// <summary>Cartella dove il DataServer trova (e carica) i file Parquet per analisi offline.
    /// Tipicamente i file vengono copiati qui dal DataService (NAS, sync, copia manuale).</summary>
    public string ParquetArchivePath { get; set; } = "./data/archives";

    // ── Retrocompatibilità ──

    /// <summary>Alias per ParquetOutputPath (retrocompatibilità).</summary>
    public string DiskPath { get => ParquetOutputPath; set => ParquetOutputPath = value; }

    /// <summary>Alias per ParquetArchivePath (retrocompatibilità).</summary>
    public string ArchivePath { get => ParquetArchivePath; set => ParquetArchivePath = value; }

    /// <summary>Cartella dove il DataServer trova file Parquet storici per analisi offline.
    /// I file vengono letti su richiesta via loadChannels senza caricarli nel ring buffer.</summary>
    public string? HistoricalDataPath { get; set; }

    public string FileFormat { get; set; } = "Parquet";
}

public sealed class SourceConnectionOptions
{
    public bool Enabled { get; set; } = true;
    public required string Id { get; set; }
    public required string Url { get; set; }
    public string? ApiKey { get; set; }

    /// <summary>When true, discover all tags from the upstream node automatically (ignores Tags).</summary>
    public bool AutoDiscovery { get; set; } = true;

    /// <summary>Explicit list of tags to subscribe. Ignored when AutoDiscovery is true.</summary>
    public string[] Tags { get; set; } = [];

    public int ForwardIntervalMs { get; set; } = 2000;
    public string Compression { get; set; } = "none";
    public bool BatchMode { get; set; }
    public bool ChunkSync { get; set; }
    public int ChunkSyncMaxBandwidthKbps { get; set; } = 100;
}

public sealed class UdpDestinationOptions
{
    public required string Id { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; } = 9200;
    public string[] Sources { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public int DownsampleMs { get; set; } = 2000;
    public int MaxPacketBytes { get; set; } = 1400;
}

public sealed class UdpReceiverOptions
{
    public bool Enabled { get; set; } = true;
    public int ListenPort { get; set; } = 9200;

    /// <summary>When true, accept any source from the UDP stream automatically (ignores Tags).</summary>
    public bool AutoDiscovery { get; set; } = true;

    /// <summary>Explicit list of tags to accept. Ignored when AutoDiscovery is true.</summary>
    public string[] Tags { get; set; } = [];
}
