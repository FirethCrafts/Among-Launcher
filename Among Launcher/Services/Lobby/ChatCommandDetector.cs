namespace AmongLauncher.Services.Lobby;

public enum ChatCommand
{
    None,
    Repost,
    Disband,
    PostLobby
}

public record ChatCommandResult(ChatCommand Command, string? Argument);

public static class ChatCommandDetector
{
    public static ChatCommandResult Detect(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new ChatCommandResult(ChatCommand.None, null);

        var trimmed = message.Trim();

        if (trimmed.Equals("/repost", StringComparison.OrdinalIgnoreCase))
            return new ChatCommandResult(ChatCommand.Repost, null);

        if (trimmed.Equals("/disband", StringComparison.OrdinalIgnoreCase))
            return new ChatCommandResult(ChatCommand.Disband, null);

        if (trimmed.StartsWith("/postlobby", StringComparison.OrdinalIgnoreCase))
        {
            var arg = trimmed.Length > "/postlobby".Length
                ? trimmed["/postlobby".Length..].Trim()
                : null;
            return new ChatCommandResult(ChatCommand.PostLobby, arg);
        }

        return new ChatCommandResult(ChatCommand.None, null);
    }
}
