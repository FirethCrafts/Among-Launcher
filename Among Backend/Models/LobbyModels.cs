namespace AmongBackend.Models;

public record ModSetEntry(string FileName, string DownloadUrl, string? Sha256, string? Version);

public record CreateLobbyRequest(
    string Code,
    string Region,
    string RegionIp,
    int RegionPort,
    List<ModSetEntry> ModSet,
    string? HostUserId);

public record LobbyResponse(
    string Code,
    string Region,
    string RegionIp,
    int RegionPort,
    List<ModSetEntry> ModSet,
    string? HostUserId,
    int PlayerCount);

public record LobbyPlayer(string DiscordUserId, string? PlayerName, bool IsHost);

public class Lobby
{
    public required string Code { get; init; }
    public string Region { get; set; } = "";
    public string RegionIp { get; set; } = "";
    public int RegionPort { get; set; }
    public List<ModSetEntry> ModSet { get; set; } = new();
    public string? HostUserId { get; set; }
    public int PlayerCount { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastHeartbeatAt { get; set; } = DateTimeOffset.UtcNow;
    public ulong? DiscordMessageId { get; set; }
}
