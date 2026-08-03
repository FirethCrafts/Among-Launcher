using System.Net;
using System.Net.WebSockets;
using AmongBackend.Models;
using AmongBackend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<LobbyStore>();
builder.Services.AddSingleton<WebSocketHub>();
builder.Services.AddSingleton<DiscordNotifier>();
builder.Services.AddHostedService<LobbyExpiryService>();
builder.Services.AddHttpClient<DiscordNotifier>();

var app = builder.Build();

app.UseWebSockets();

var store = app.Services.GetRequiredService<LobbyStore>();
var hub = app.Services.GetRequiredService<WebSocketHub>();
var notifier = app.Services.GetRequiredService<DiscordNotifier>();

app.MapGet("/", () => "Among Backend is running.");

// ---------------------------------------------------------------------------
// REST: lobby lifecycle
// ---------------------------------------------------------------------------

// POST /lobby — create/register a lobby
app.MapPost("/lobby", async (CreateLobbyRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Code))
        return Results.BadRequest(new { error = "code is required" });

    var lobby = new Lobby
    {
        Code = req.Code.ToUpperInvariant(),
        Region = req.Region,
        RegionIp = req.RegionIp,
        RegionPort = req.RegionPort,
        ModSet = req.ModSet ?? new List<ModSetEntry>(),
        HostUserId = req.HostUserId,
        PlayerCount = 0
    };

    // Mod-change detection: if this host previously ran a lobby with connected
    // launchers and the mod set differs, push rejoin to everyone from before.
    var previous = store.GetLatestByHost(req.HostUserId);
    var previousHadGuests = previous != null && hub.Count(previous.Code) > 0;
    var modSetChanged = previous != null && !SameModSet(previous.ModSet, lobby.ModSet);

    var created = store.TryAdd(lobby);
    if (created)
    {
        lobby.DiscordMessageId = await notifier.PostLobbyAsync(lobby);

        if (previousHadGuests && modSetChanged && !string.Equals(previous!.Code, lobby.Code, StringComparison.OrdinalIgnoreCase))
        {
            await hub.PushRejoinAsync(previous.Code, lobby.ModSet.ToArray(),
                lobby.Region, lobby.RegionIp, lobby.RegionPort, ct);
        }
        return Results.Ok(ToResponse(lobby));
    }

    // Existing lobby with the same code: refresh it instead.
    var existing = store.Get(lobby.Code)!;
    existing.Region = lobby.Region;
    existing.RegionIp = lobby.RegionIp;
    existing.RegionPort = lobby.RegionPort;
    existing.ModSet = lobby.ModSet;
    existing.HostUserId = lobby.HostUserId;
    existing.LastHeartbeatAt = DateTimeOffset.UtcNow;
    await notifier.EditLobbyAsync(existing);
    return Results.Ok(ToResponse(existing));
});

// GET /lobby/{code} — fetch a lobby
app.MapGet("/lobby/{code}", (string code) =>
{
    var lobby = store.Get(code);
    return lobby == null
        ? Results.NotFound(new { error = "lobby not found" })
        : Results.Ok(ToResponse(lobby));
});

// POST /lobby/{code}/repost — refresh the Discord embed
app.MapPost("/lobby/{code}/repost", async (string code) =>
{
    var lobby = store.Get(code);
    if (lobby == null) return Results.NotFound(new { error = "lobby not found" });
    await notifier.EditLobbyAsync(lobby);
    return Results.Ok();
});

// POST /lobby/{code}/kick — kick one player via WebSocket push
app.MapPost("/lobby/{code}/kick", async (string code, KickRequest body, CancellationToken ct) =>
{
    var lobby = store.Get(code);
    if (lobby == null) return Results.NotFound(new { error = "lobby not found" });
    await hub.PushKickAsync(code, body.TargetUserId, body.Reason, ct);
    return Results.Ok();
});

// DELETE /lobby/{code} — disband/delete a lobby
app.MapDelete("/lobby/{code}", async (string code) =>
{
    if (store.TryRemove(code, out var lobby))
    {
        await notifier.DeleteLobbyAsync(lobby.DiscordMessageId);
        return Results.Ok();
    }
    return Results.NotFound(new { error = "lobby not found" });
});

// POST /lobby/{code}/heartbeat — keepalive from the host launcher
app.MapPost("/lobby/{code}/heartbeat", (string code, HeartbeatRequest body) =>
{
    if (!store.Touch(code)) return Results.NotFound(new { error = "lobby not found" });
    return Results.Ok();
});

// POST /lobby/{code}/players — report player count (embed updates)
app.MapPost("/lobby/{code}/players", async (string code, PlayersRequest body) =>
{
    var lobby = store.Get(code);
    if (lobby == null) return Results.NotFound(new { error = "lobby not found" });
    lobby.PlayerCount = body.PlayerCount;
    await notifier.EditLobbyAsync(lobby);
    return Results.Ok();
});

// ---------------------------------------------------------------------------
// WebSocket: live kick / rejoin push to connected launchers
// ---------------------------------------------------------------------------

app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        return;
    }

    var lobbyCode = context.Request.Query["code"].ToString();
    var userId = ResolveUserId(context.Request);

    if (string.IsNullOrWhiteSpace(lobbyCode))
    {
        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connectionId = hub.Register(lobbyCode, userId, socket);

    try
    {
        var buffer = new byte[1024 * 4];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                break;
        }
    }
    catch { }
    finally
    {
        hub.Unregister(lobbyCode, connectionId);
    }
});

app.Run();

static string ResolveUserId(HttpRequest request)
{
    var auth = request.Headers.Authorization.ToString();
    if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        var token = auth["Bearer ".Length..].Trim();
        if (!string.IsNullOrEmpty(token))
            return "user:" + token[..Math.Min(8, token.Length)];
    }
    return "anon";
}

static bool SameModSet(List<ModSetEntry>? a, List<ModSetEntry>? b)
{
    if (a == null && b == null) return true;
    if (a == null || b == null) return false;
    var namesA = a.Select(m => m.FileName).OrderBy(n => n).ToArray();
    var namesB = b.Select(m => m.FileName).OrderBy(n => n).ToArray();
    return namesA.SequenceEqual(namesB);
}

static LobbyResponse ToResponse(Lobby lobby) => new(
    lobby.Code, lobby.Region, lobby.RegionIp, lobby.RegionPort,
    lobby.ModSet, lobby.HostUserId, lobby.PlayerCount);

public record KickRequest(string TargetUserId, string? Reason);
public record PlayersRequest(int PlayerCount);
public record HeartbeatRequest(string Code, string HostUserId);
