namespace AmongLauncher.Models;

public record CreateLobbyRequest(string Code, string Region, string RegionIp, int RegionPort, List<ModSetEntry> ModSet, string? HostUserId);
public record LobbyResponse(string Code, string Region, string RegionIp, int RegionPort, List<ModSetEntry> ModSet, string? HostUserId, int PlayerCount);
public record LobbyPlayer(string DiscordUserId, string? PlayerName, bool IsHost);
