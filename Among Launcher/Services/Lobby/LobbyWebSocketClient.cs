using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AmongLauncher.Config;
using AmongLauncher.Models;

namespace AmongLauncher.Services.Lobby;

public record RejoinCommand(string LobbyCode, List<ModSetEntry> ModSet, string Region, string RegionIp, int RegionPort);

public class LobbyWebSocketClient
{
    private readonly LauncherConfig _config;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

    public event EventHandler<string>? Kicked;
    public event EventHandler<RejoinCommand>? Rejoin;

    public LobbyWebSocketClient(LauncherConfig config) => _config = config;

    public async Task ConnectAsync(string lobbyCode, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempt = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var uri = $"{_config.BackendWssUrl}?code={lobbyCode}";
                _ws = new ClientWebSocket();
                if (!string.IsNullOrEmpty(_config.DiscordAccessToken))
                    _ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.DiscordAccessToken}");
                await _ws.ConnectAsync(new Uri(uri), _cts.Token);
                await ReceiveLoopAsync(_ws, _cts.Token);
            }
            catch { }
            finally { _ws?.Dispose(); _ws = null; }
            if (_cts.IsCancellationRequested) break;
            attempt++;
            var delay = Math.Min(5, attempt) * 2000;
            try { await Task.Delay(delay, _cts.Token); } catch { break; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(ms.ToArray());
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString() ?? "";

            if (type == "kick")
            {
                var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : "";
                Kicked?.Invoke(this, reason ?? "");
            }
            else if (type == "rejoin")
            {
                var p = doc.RootElement.GetProperty("payload");
                Rejoin?.Invoke(this, new RejoinCommand(
                    p.GetProperty("lobbyCode").GetString() ?? "",
                    DeserializeMods(p),
                    p.GetProperty("region").GetString() ?? "",
                    p.GetProperty("regionIp").GetString() ?? "",
                    p.GetProperty("regionPort").GetInt32()));
            }
        }
    }

    private static List<ModSetEntry> DeserializeMods(JsonElement p)
    {
        var mods = new List<ModSetEntry>();
        if (p.TryGetProperty("modSet", out var arr))
        {
            foreach (var m in arr.EnumerateArray())
            {
                mods.Add(new ModSetEntry
                {
                    FileName = m.GetProperty("fileName").GetString() ?? "",
                    DownloadUrl = m.GetProperty("downloadUrl").GetString() ?? "",
                    Sha256 = m.TryGetProperty("sha256", out var s) ? s.GetString() : null,
                    Version = m.TryGetProperty("version", out var v) ? v.GetString() : null
                });
            }
        }
        return mods;
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _ws?.Dispose();
    }
}
