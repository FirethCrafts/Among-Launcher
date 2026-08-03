using AmongLauncher.Models;

namespace AmongLauncher.Services.Lobby;

public record JoinOutcome(bool Started, string? Error);

public class LobbyJoinService
{
    private readonly Func<string, CancellationToken, Task<LobbyInfo?>> _getLobby;
    private readonly Func<List<ModSetEntry>, Task<bool>> _ensureSetup;
    private readonly Func<Task> _killGame;
    private readonly Func<Task> _launchGame;
    private readonly Func<Task<bool>> _waitForGameReady;
    private readonly Func<LobbyInfo, Task> _sendJoinLobby;
    private readonly ModSetSync _modSetSync;

    public LobbyJoinService(
        Func<string, CancellationToken, Task<LobbyInfo?>> getLobby,
        Func<List<ModSetEntry>, Task<bool>> ensureSetup,
        Func<Task> killGame,
        Func<Task> launchGame,
        Func<Task<bool>> waitForGameReady,
        Func<LobbyInfo, Task> sendJoinLobby,
        ModSetSync modSetSync)
    {
        _getLobby = getLobby;
        _ensureSetup = ensureSetup;
        _killGame = killGame;
        _launchGame = launchGame;
        _waitForGameReady = waitForGameReady;
        _sendJoinLobby = sendJoinLobby;
        _modSetSync = modSetSync;
    }

    public async Task<JoinOutcome> JoinLobbyAsync(string code, CancellationToken ct)
    {
        var lobby = await _getLobby(code, ct);
        if (lobby == null)
            return new JoinOutcome(false, "Lobby not found");

        var setupOk = await _ensureSetup(lobby.ModSet);
        if (!setupOk)
            return new JoinOutcome(false, "Modded Among Us is not installed. Run one-click setup first.");

        var missing = await _modSetSync.DiffAsync(lobby.ModSet, ct);
        if (missing.Count > 0)
        {
            await _killGame();
            await _modSetSync.InstallAsync(missing, null, ct);
        }

        await _launchGame();

        var ready = await _waitForGameReady();
        if (!ready)
            return new JoinOutcome(false, "Game did not become ready in time");

        await _sendJoinLobby(lobby);
        return new JoinOutcome(true, null);
    }
}
