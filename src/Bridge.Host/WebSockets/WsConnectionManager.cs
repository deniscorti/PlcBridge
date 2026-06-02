using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Bridge.Host.WebSockets;

/// <summary>
/// Tracks active WebSocket connections and provides send capabilities.
/// </summary>
public sealed class WsConnectionManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public string AddConnection(WebSocket socket)
    {
        var id = Guid.NewGuid().ToString("N");
        _connections.TryAdd(id, socket);
        return id;
    }

    public void RemoveConnection(string id)
    {
        _connections.TryRemove(id, out _);
    }

    public async Task SendAsync<T>(string connectionId, T message, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(connectionId, out var socket)) return;
        if (socket.State != WebSocketState.Open) return;

        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOpts);
        await socket.SendAsync(json, WebSocketMessageType.Text, true, ct);
    }

    public async Task SendToManyAsync<T>(IReadOnlyList<string> connectionIds, T message, CancellationToken ct = default)
    {
        if (connectionIds.Count == 0) return;
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOpts);

        foreach (var connId in connectionIds)
        {
            if (!_connections.TryGetValue(connId, out var socket)) continue;
            if (socket.State != WebSocketState.Open) continue;

            try
            {
                await socket.SendAsync(json, WebSocketMessageType.Text, true, ct);
            }
            catch
            {
                // Connection may have closed; will be cleaned up on next receive
            }
        }
    }

    public async Task BroadcastAsync<T>(T message, CancellationToken ct = default)
    {
        if (_connections.IsEmpty) return;
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOpts);

        foreach (var (_, socket) in _connections)
        {
            if (socket.State != WebSocketState.Open) continue;
            try
            {
                await socket.SendAsync(json, WebSocketMessageType.Text, true, ct);
            }
            catch { }
        }
    }

    public int ConnectionCount => _connections.Count;
}
