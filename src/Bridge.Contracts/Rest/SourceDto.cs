namespace Bridge.Contracts.Rest;

public sealed record SourceDto(string Id, IReadOnlyList<TagDto> Tags);

public sealed record TagDto(string Name, string Kind, string? Address = null, int? PollMs = null);

public sealed record TagValueDto(string Source, string Tag, object? Value, DateTimeOffset Timestamp, uint MsgId);

public sealed record WriteRequest(object Value);

public sealed record HealthDto(string Status, string Mode, long UptimeSeconds);

public sealed record StatusDto(
    string Mode,
    long UptimeSeconds,
    IReadOnlyList<SourceStatusDto> Sources);

public sealed record SourceStatusDto(
    string Id,
    bool Connected,
    int TagCount);
