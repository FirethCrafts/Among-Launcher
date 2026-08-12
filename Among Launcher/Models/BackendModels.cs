using System.Text.Json.Serialization;

namespace AmongLauncher.Models;

public record CreateLobbyRequest(
    string Code,
    string Region,
    string Host,
    [property: JsonPropertyName("mod_type")] string ModType,
    List<ModInfoEntry> Mods,
    [property: JsonPropertyName("max_players")] int MaxPlayers = 15,
    [property: JsonPropertyName("game_version")] string? GameVersion = null,
    [property: JsonPropertyName("map_name")] string? MapName = null,
    [property: JsonPropertyName("language")] string? Language = null,
    [property: JsonPropertyName("chat_type")] string? ChatType = null);

public record LobbyResponse(
    string Code,
    string Region,
    string Host,
    [property: JsonPropertyName("mod_type")] string ModType,
    List<ModInfoEntry> Mods,
    List<PlayerInfoEntry> Players,
    [property: JsonPropertyName("max_players")] int MaxPlayers = 15);

public record LobbyDetailedResponse(
    string Code,
    string Region,
    string Host,
    [property: JsonPropertyName("mod_type")] string ModType,
    string Status,
    List<ModInfoEntry> Mods,
    List<PlayerInfoEntry> Players,
    [property: JsonPropertyName("last_heartbeat")] DateTime LastHeartbeat,
    [property: JsonPropertyName("max_players")] int MaxPlayers = 15,
    [property: JsonPropertyName("created_at")] DateTime? CreatedAt = null,
    [property: JsonPropertyName("game_version")] string? GameVersion = null,
    [property: JsonPropertyName("map_name")] string? MapName = null,
    [property: JsonPropertyName("language")] string? Language = null,
    [property: JsonPropertyName("chat_type")] string? ChatType = null);

public record ModInfoEntry(
    string Name,
    string? Version,
    [property: JsonPropertyName("file_hash")] string? FileHash,
    [property: JsonPropertyName("download_url")] string? DownloadUrl = null);

public record PlayerInfoEntry(
    string Id,
    string Name,
    [property: JsonPropertyName("is_host")] bool IsHost,
    int? Level = null,
    int? Ping = null);

public record LobbyPlayer(string DiscordUserId, string? PlayerName, bool IsHost);
