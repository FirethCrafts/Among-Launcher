using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AmongLauncher.Config;
using AmongLauncher.Services;

namespace AmongLauncher.Services.Lobby;

public record LobbyBotPayload(
    string Code,
    string Region,
    string Host,
    string Mod,
    string RoleId,
    string[] AppliedTags);

public record LobbyBotResponse(bool Ok, string? Error);

public class LobbyBotClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private ClientWebSocket? _ws;
    private CancellationTokenSource _cts = new();

    public async Task ConnectAsync(string endpoint)
    {
        try
        {
            _ws?.Dispose();
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(endpoint), _cts.Token);
        }
        catch
        {
        }
    }

    public async Task<LobbyBotResponse?> SendLobbyCreatedAsync(LobbyBotPayload payload)
    {
        try
        {
            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }

            if (_ws == null || _ws.State != WebSocketState.Open)
            {
                await ConnectAsync(DefaultEndpoint());
            }

            if (_ws == null || _ws.State != WebSocketState.Open)
            {
                return null;
            }

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);

            var response = await ReadResponseAsync();
            if (response != null)
            {
                LauncherLog.Write($"Lobby bot announce: ok={response.Ok} error={response.Error ?? "none"}");
            }

            return response;
        }
        catch (Exception ex)
        {
            LauncherLog.Write($"Lobby bot announce failed: {ex.Message}");
            return null;
        }
    }

    public void Disconnect()
    {
        _cts.Cancel();
        _ws?.Dispose();
        _ws = null;
    }

    private static string DefaultEndpoint()
    {
        return LauncherConfig.Load().BotWsEndpoint;
    }

    private async Task<LobbyBotResponse?> ReadResponseAsync()
    {
        try
        {
            var buffer = new byte[4096];
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws!.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(ms.ToArray());
            using var doc = JsonDocument.Parse(text);
            var ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            var error = doc.RootElement.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
            return new LobbyBotResponse(ok, error);
        }
        catch (Exception ex)
        {
            LauncherLog.Write($"Lobby bot response parse failed: {ex.Message}");
            return null;
        }
    }
}
