using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AmongBackend.Services;

public class WebSocketHub
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WebSocket>> _connections =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a launcher's WebSocket for a lobby. The user id identifies the
    /// launcher (Discord user id when authenticated, otherwise the connection id).
    /// </summary>
    public string Register(string lobbyCode, string userId, WebSocket socket)
    {
        var bucket = _connections.GetOrAdd(lobbyCode, _ => new ConcurrentDictionary<string, WebSocket>());
        var connectionId = Guid.NewGuid().ToString("N")[..8];
        bucket[connectionId] = socket;
        return connectionId;
    }

    public void Unregister(string lobbyCode, string connectionId)
    {
        if (!_connections.TryGetValue(lobbyCode, out var bucket)) return;
        bucket.TryRemove(connectionId, out _);
        if (bucket.IsEmpty)
            _connections.TryRemove(lobbyCode, out _);
    }

    public async Task PushKickAsync(string lobbyCode, string targetUserId, string? reason, CancellationToken ct)
    {
        var message = JsonSerializer.Serialize(new { type = "kick", reason = reason ?? "" });
        await SendToAllAsync(lobbyCode, message, ct);
    }

    public async Task PushRejoinAsync(string lobbyCode, Models.ModSetEntry[] modSet,
        string region, string regionIp, int regionPort, CancellationToken ct)
    {
        var message = JsonSerializer.Serialize(new
        {
            type = "rejoin",
            payload = new
            {
                lobbyCode,
                modSet,
                region,
                regionIp,
                regionPort
            }
        });
        await SendToAllAsync(lobbyCode, message, ct);
    }

    public int Count(string lobbyCode) =>
        _connections.TryGetValue(lobbyCode, out var bucket) ? bucket.Count : 0;

    private async Task SendToAllAsync(string lobbyCode, string json, CancellationToken ct)
    {
        if (!_connections.TryGetValue(lobbyCode, out var bucket)) return;

        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var (connectionId, socket) in bucket.ToArray())
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
                }
            }
            catch
            {
                bucket.TryRemove(connectionId, out _);
            }
        }
    }
}
