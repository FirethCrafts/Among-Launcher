using System.Net.Http;
using System.Net.Http.Json;
using AmongLauncher.Config;
using AmongLauncher.Models;

namespace AmongLauncher.Services.Lobby;

public class LobbyBackendClient
{
    private readonly HttpClient _http;
    private readonly LauncherConfig _config;

    public static bool IsConfigured(LauncherConfig config) =>
        !string.IsNullOrWhiteSpace(config.ServerUrl) &&
        !config.ServerUrl.Contains("yourserver.com", StringComparison.OrdinalIgnoreCase);

    public LobbyBackendClient(HttpClient http, LauncherConfig config)
    {
        _http = http;
        _config = config;
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/");
    }

    private void ApplyAuth(HttpRequestMessage msg)
    {
        if (!string.IsNullOrEmpty(_config.DiscordAccessToken))
            msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.DiscordAccessToken);
    }

    public async Task<LobbyInfo?> GetLobbyAsync(string code, CancellationToken ct)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"api/v1/lobby/{code}");
            ApplyAuth(msg);
            using var resp = await _http.SendAsync(msg, ct);

            if (!resp.IsSuccessStatusCode)
            {
                Services.LauncherLog.Write($"[Backend] GET api/v1/lobby/{code} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }

            var body = await resp.Content.ReadFromJsonAsync<LobbyResponse>(cancellationToken: ct);
            if (body == null)
            {
                Services.LauncherLog.Write($"[Backend] GET api/v1/lobby/{code} returned an empty body.");
                return null;
            }

            return new LobbyInfo
            {
                Code = body.Code,
                Region = body.Region,
                ModSet = (body.Mods ?? new List<ModInfoEntry>())
                    .Select(m => new ModSetEntry { FileName = m.Name, Version = m.Version, Sha256 = m.FileHash })
                    .ToList(),
                Host = body.Host,
                PlayerCount = body.Players?.Count ?? 0
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            Services.LauncherLog.Write($"[Backend] GET api/v1/lobby/{code} timed out.");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Services.LauncherLog.Write($"[Backend] GET api/v1/lobby/{code} failed: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> CreateLobbyAsync(CreateLobbyRequest req, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "api/v1/lobby") { Content = JsonContent.Create(req) };
        ApplyAuth(msg);
        using var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public Task<bool> RepostAsync(string code, CancellationToken ct) =>
        PostNoContent($"api/v1/lobby/{code}/repost", ct);

    public Task<bool> KickAsync(string code, string targetUserId, CancellationToken ct) =>
        PostNoContent($"api/v1/lobby/{code}/kick", ct, new { player_id = targetUserId });

    public Task<bool> DisbandAsync(string code, CancellationToken ct) =>
        DeleteNoContent($"api/v1/lobby/{code}", ct);

    public Task<bool> HeartbeatAsync(string code, string hostUserId, CancellationToken ct) =>
        PostNoContent($"api/v1/lobby/{code}/heartbeat", ct);

    private async Task<bool> PostNoContent(string path, CancellationToken ct, object? body = null)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, path);
            ApplyAuth(msg);
            if (body != null) msg.Content = JsonContent.Create(body);
            using var resp = await _http.SendAsync(msg, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<bool> DeleteNoContent(string path, CancellationToken ct)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Delete, path);
            ApplyAuth(msg);
            using var resp = await _http.SendAsync(msg, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
