namespace AmongLauncher.Services.Lobby;

public class LobbyCommandService
{
    private readonly Func<Task> _killGame;
    private readonly Func<RejoinCommand, Task> _rejoin;

    public LobbyCommandService(LobbyWebSocketClient ws, Func<Task> killGame, Func<RejoinCommand, Task> rejoin)
    {
        _killGame = killGame;
        _rejoin = rejoin;
        ws.Kicked += (_, _) => _ = _killGame();
        ws.Rejoin += async (_, cmd) => await _rejoin(cmd);
    }
}
