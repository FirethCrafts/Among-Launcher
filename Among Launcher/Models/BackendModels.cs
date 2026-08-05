namespace AmongLauncher.Models;

public record CreateLobbyRequest(string Code, string Region, string Host, string ModType, List<ModInfoEntry> Mods);
public record LobbyResponse(string Code, string Region, string Host, string ModType, List<ModInfoEntry> Mods, List<PlayerInfoEntry> Players);
public record ModInfoEntry(string Name, string? Version, string? FileHash);
public record PlayerInfoEntry(string Id, string Name, bool IsHost);
public record LobbyPlayer(string DiscordUserId, string? PlayerName, bool IsHost);
