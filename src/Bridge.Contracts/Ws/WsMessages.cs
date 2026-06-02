using System.Text.Json.Serialization;

namespace Bridge.Contracts.Ws;

// ── Client → Server ──

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(WsSubscribe), "subscribe")]
[JsonDerivedType(typeof(WsUnsubscribe), "unsubscribe")]
[JsonDerivedType(typeof(WsRead), "read")]
[JsonDerivedType(typeof(WsWrite), "write")]
[JsonDerivedType(typeof(WsPing), "ping")]
[JsonDerivedType(typeof(WsGetSources), "getSources")]
[JsonDerivedType(typeof(WsGetStatus), "getStatus")]
[JsonDerivedType(typeof(WsQueryTelemetry), "queryTelemetry")]
[JsonDerivedType(typeof(WsQueryEvents), "queryEvents")]
[JsonDerivedType(typeof(WsQueryAlarms), "queryAlarms")]
[JsonDerivedType(typeof(WsBridgeCommand), "bridgeCommand")]
[JsonDerivedType(typeof(WsSourceCommand), "sourceCommand")]
[JsonDerivedType(typeof(WsAckAlarm), "ackAlarm")]
[JsonDerivedType(typeof(WsReplay), "replay")]
[JsonDerivedType(typeof(WsReplayStop), "replayStop")]
[JsonDerivedType(typeof(WsChunkRequest), "chunkRequest")]
public abstract record WsClientMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

public sealed record WsSubscribe : WsClientMessage
{
    public string? Source { get; init; }
    public string[]? Tags { get; init; }
    public string[]? Kinds { get; init; }
    public int? DownsampleMs { get; init; }
    public uint? Since { get; init; }

    /// <summary>When true, enables compact push mode: server sends a layout once, then only value arrays.</summary>
    public bool Compact { get; init; }
}

public sealed record WsUnsubscribe : WsClientMessage
{
    public string? Source { get; init; }
    public string[]? Tags { get; init; }
}

public sealed record WsRead : WsClientMessage
{
    public required string Source { get; init; }
    public required string Tag { get; init; }
}

public sealed record WsWrite : WsClientMessage
{
    public required string Source { get; init; }
    public required string Tag { get; init; }
    public required object Value { get; init; }
}

public sealed record WsPing : WsClientMessage;
public sealed record WsGetSources : WsClientMessage;
public sealed record WsGetStatus : WsClientMessage;

public sealed record WsQueryTelemetry : WsClientMessage
{
    public required string Source { get; init; }
    public string[]? Tags { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public int Limit { get; init; } = 1000;
}

public sealed record WsQueryEvents : WsClientMessage
{
    public required string Source { get; init; }
    public string[]? Tags { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public int Limit { get; init; } = 1000;
}

public sealed record WsQueryAlarms : WsClientMessage
{
    public required string Source { get; init; }
    public string[]? Tags { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public bool? Active { get; init; }
    public bool? Acknowledged { get; init; }
    public int Limit { get; init; } = 1000;
}

public sealed record WsBridgeCommand : WsClientMessage
{
    public required string Command { get; init; }
    public Dictionary<string, object?>? Params { get; init; }
}

public sealed record WsSourceCommand : WsClientMessage
{
    public required string Source { get; init; }
    public required string Command { get; init; }
    public Dictionary<string, object?>? Params { get; init; }
}

public sealed record WsAckAlarm : WsClientMessage
{
    public required string Source { get; init; }
    public string? Tag { get; init; }
    public string[]? Tags { get; init; }
}

public sealed record WsReplay : WsClientMessage
{
    public required string Source { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public double Speed { get; init; } = 1.0;
}

public sealed record WsReplayStop : WsClientMessage;

public sealed record WsChunkRequest : WsClientMessage
{
    public required string Source { get; init; }
    public required string ChunkId { get; init; }
}

// ── Server → Client ──

public sealed record WsResponse
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "response";

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonExtensionData]
    public Dictionary<string, object?>? Extra { get; init; }
}

public sealed record WsPushTelemetry
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "telemetry";
    public required string Source { get; init; }
    public required string Tag { get; init; }
    public required object? Value { get; init; }
    public required DateTimeOffset Ts { get; init; }
    public required uint MsgId { get; init; }
}

public sealed record WsPushEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "event";
    public required string Source { get; init; }
    public required string Tag { get; init; }
    public required object? Value { get; init; }
    public required DateTimeOffset Ts { get; init; }
    public required uint MsgId { get; init; }
}

public sealed record WsPushAlarm
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "alarm";
    public required string Source { get; init; }
    public required string Tag { get; init; }
    public required bool Active { get; init; }
    public required bool Acknowledged { get; init; }
    public string? Severity { get; init; }
    public required DateTimeOffset Ts { get; init; }
    public required uint MsgId { get; init; }
}

public sealed record WsPushConnection
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "connection";
    public required string Source { get; init; }
    public required string State { get; init; }
    public string? Reason { get; init; }
}

public sealed record WsPong
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "pong";
}

public sealed record WsError
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "error";

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    public required string Code { get; init; }
    public required string Message { get; init; }
}
