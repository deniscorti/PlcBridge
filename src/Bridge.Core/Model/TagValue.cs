namespace Bridge.Core.Model;

/// <summary>
/// A single data point: the value of a tag at a specific moment in time.
/// </summary>
public sealed record TagValue
{
    public required string Source { get; init; }
    public required string Tag { get; init; }
    public required DataKind Kind { get; init; }
    public required object? Value { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required uint MsgId { get; init; }
}
