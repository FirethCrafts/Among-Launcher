using System.Text.Json.Serialization;

namespace AmongLauncher.Models;

public record CreateLobbyRequest(
    string Code,
    string Region,
    string Host,
    [property: JsonPropertyName("mod_type")] string ModType,
    List<ModInfoEntry> Mods,
    [property: JsonPropertyName("max_players")] int MaxPlayers = 15);

public record LobbyResponse(
    string Code,
    string Region,
    string Host,
    [property: JsonPropertyName("mod_type")] string ModType,
    List<ModInfoEntry> Mods,
    List<PlayerInfoEntry> Players,
    [property: JsonPropertyName("max_players")] int MaxPlayers = 15);

public record ModInfoEntry(
    string Name,
    string? Version,
    [property: JsonPropertyName("file_hash")] string? FileHash);

public record PlayerInfoEntry(
    string Id,
    string Name,
    [property: JsonPropertyName("is_host")] bool IsHost);

public record LobbyPlayer(string DiscordUserId, string? PlayerName, bool IsHost);
