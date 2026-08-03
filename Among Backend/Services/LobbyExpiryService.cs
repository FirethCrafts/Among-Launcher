namespace AmongBackend.Services;

/// <summary>
/// Periodically removes lobbies whose host stopped sending heartbeats
/// (crash / alt-F4), cleaning up their state and Discord embed.
/// </summary>
public class LobbyExpiryService : BackgroundService
{
    private readonly LobbyStore _store;
    private readonly WebSocketHub _hub;
    private readonly DiscordNotifier _notifier;
    private readonly ILogger<LobbyExpiryService> _log;
    private readonly TimeSpan _grace;

    public LobbyExpiryService(
        LobbyStore store,
        WebSocketHub hub,
        DiscordNotifier notifier,
        ILogger<LobbyExpiryService> log,
        IConfiguration config)
    {
        _store = store;
        _hub = hub;
        _notifier = notifier;
        _log = log;
        _grace = TimeSpan.FromSeconds(config.GetValue("Lobby:HeartbeatGraceSeconds", 90));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

            var cutoff = DateTimeOffset.UtcNow - _grace;
            foreach (var lobby in _store.All.Where(l => l.LastHeartbeatAt < cutoff).ToArray())
            {
                if (_store.TryRemove(lobby.Code, out _))
                {
                    _log.LogWarning("Lobby {Code} expired (no heartbeat for {Grace}).", lobby.Code, _grace);
                    await _notifier.DeleteLobbyAsync(lobby.DiscordMessageId);
                }
            }
        }
    }
}
