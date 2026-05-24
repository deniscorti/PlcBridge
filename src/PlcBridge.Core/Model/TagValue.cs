namespace PlcBridge.Core.Model;

public sealed record TagValue(string PlcId, string Tag, object? Value, DateTimeOffset Timestamp);
