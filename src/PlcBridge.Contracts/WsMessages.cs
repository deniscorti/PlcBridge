namespace PlcBridge.Contracts;

public sealed record WsSubscribe(string Op, string[] Tags);
public sealed record WsWrite(string Op, string Tag, object Value);
public sealed record WsValueOut(string Type, string Tag, object? Value, DateTimeOffset Ts);
