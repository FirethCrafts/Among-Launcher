using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace AmongBackend.Services;

/// <summary>
/// Posts and live-edits the Discord invite embed. Uses a webhook URL when
/// configured; otherwise it's a no-op so the backend still works locally.
/// </summary>
public class DiscordNotifier
{
    private readonly HttpClient _http;
    private readonly ILogger<DiscordNotifier> _log;
    private readonly string? _webhookUrl;

    public DiscordNotifier(HttpClient http, ILogger<DiscordNotifier> log, IConfiguration config)
    {
        _http = http;
        _log = log;
        _webhookUrl = config["Discord:WebhookUrl"];
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_webhookUrl);

    public async Task<ulong?> PostLobbyAsync(Models.Lobby lobby)
    {
        if (!Enabled) return null;

        var payload = BuildEmbed(lobby, "**A player is hosting a lobby — click to join!**");
        try
        {
            using var resp = await _http.PostAsJsonAsync(_webhookUrl, payload);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Discord embed post failed: {Status}", resp.StatusCode);
                return null;
            }
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (body.TryGetProperty("id", out var id))
                return ulong.Parse(id.GetString() ?? "0");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Discord embed post failed");
        }
        return null;
    }

    public async Task EditLobbyAsync(Models.Lobby lobby)
    {
        if (!Enabled || lobby.DiscordMessageId == null) return;

        var payload = BuildEmbed(lobby, "**A player is hosting a lobby — click to join!**");
        try
        {
            using var resp = await _http.PatchAsJsonAsync($"{_webhookUrl}/messages/{lobby.DiscordMessageId}", payload);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("Discord embed edit failed: {Status}", resp.StatusCode);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Discord embed edit failed");
        }
    }

    public async Task DeleteLobbyAsync(ulong? messageId)
    {
        if (!Enabled || messageId == null) return;

        try
        {
            using var resp = await _http.DeleteAsync($"{_webhookUrl}/messages/{messageId}");
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("Discord embed delete failed: {Status}", resp.StatusCode);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Discord embed delete failed");
        }
    }

    private static object BuildEmbed(Models.Lobby lobby, string description)
    {
        return new
        {
            content = (string?)null,
            embeds = new[]
            {
                new
                {
                    title = $"Join lobby {lobby.Code}",
                    description,
                    color = 0xDC2626,
                    fields = new object[]
                    {
                        new { name = "Players", value = $"{lobby.PlayerCount}/15", inline = true },
                        new { name = "Region", value = lobby.Region, inline = true },
                        new { name = "Host", value = lobby.HostUserId ?? "Unknown", inline = true }
                    },
                    url = $"amonglauncher://join?code={lobby.Code}"
                }
            },
            components = new object[]
            {
                new
                {
                    type = 1,
                    components = new object[]
                    {
                        new { type = 2, style = 5, label = "Join Lobby", url = $"amonglauncher://join?code={lobby.Code}" }
                    }
                }
            }
        };
    }
}
