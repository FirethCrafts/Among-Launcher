using System.Collections.Concurrent;
using AmongBackend.Models;

namespace AmongBackend.Services;

public class LobbyStore
{
    private readonly ConcurrentDictionary<string, Lobby> _lobbies = new(StringComparer.OrdinalIgnoreCase);

    public Lobby? Get(string code) => _lobbies.TryGetValue(code, out var lobby) ? lobby : null;

    public IReadOnlyCollection<Lobby> All => _lobbies.Values.ToArray();

    public bool TryAdd(Lobby lobby) => _lobbies.TryAdd(lobby.Code, lobby);

    public bool TryRemove(string code, out Lobby lobby) => _lobbies.TryRemove(code, out lobby!);

    public bool Touch(string code)
    {
        if (_lobbies.TryGetValue(code, out var lobby))
        {
            lobby.LastHeartbeatAt = DateTimeOffset.UtcNow;
            return true;
        }
        return false;
    }

    /// <summary>Finds the most recent lobby hosted by a user, if any.</summary>
    public Lobby? GetLatestByHost(string? hostUserId)
    {
        if (string.IsNullOrEmpty(hostUserId)) return null;
        return _lobbies.Values
            .Where(l => string.Equals(l.HostUserId, hostUserId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefault();
    }
}
