namespace AmongLauncher.Services.Lobby;

public class LobbyHeartbeatService
{
    private readonly Func<string, string, CancellationToken, Task<bool>> _heartbeat;
    private CancellationTokenSource? _cts;

    public LobbyHeartbeatService(Func<string, string, CancellationToken, Task<bool>> heartbeat) => _heartbeat = heartbeat;

    public void Start(string code, string hostUserId)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            LauncherLog.Write($"[Heartbeat] Starting for lobby {code}, host={hostUserId}");
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token); }
                catch { return; }
                try
                {
                    var result = await _heartbeat(code, hostUserId, _cts.Token);
                    LauncherLog.Write($"[Heartbeat] Sent for {code}: {result}");
                }
                catch (Exception ex)
                {
                    LauncherLog.Write($"[Heartbeat] Failed for {code}: {ex.Message}");
                }
            }
        });
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
