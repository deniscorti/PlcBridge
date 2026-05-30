using Bridge.Core.Model;

namespace Bridge.Host.Configuration;

public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    public BridgeMode Mode { get; set; } = BridgeMode.DataProvider;
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
    public int InMemoryMinutes { get; set; } = 60;
    public int ChunkDurationMin { get; set; } = 5;
    public bool PersistToDisk { get; set; } = true;
    public string DiskPath { get; set; } = "./data";
    public string ArchivePath { get; set; } = "./data/archives";
    public string FileFormat { get; set; } = "Parquet";
}

public sealed class SourceConnectionOptions
{
    public required string Id { get; set; }
    public required string DataSource { get; set; }
    public required string Url { get; set; }
    public string? ApiKey { get; set; }
    public string SubscribeTags { get; set; } = "ALL";
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
    public int ListenPort { get; set; } = 9200;
    public UdpReceiverSourceOptions[] Sources { get; set; } = [];
}

public sealed class UdpReceiverSourceOptions
{
    public required string DataSource { get; set; }
    public bool Enabled { get; set; } = true;
}
