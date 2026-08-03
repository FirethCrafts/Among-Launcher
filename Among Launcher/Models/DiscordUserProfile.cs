namespace AmongLauncher.Models;

public record DiscordUserProfile(
    string Id,
    string Username,
    string? GlobalName,
    string AvatarUrl);
