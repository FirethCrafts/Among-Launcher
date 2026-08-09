namespace AmongLauncher.Models;

public class LobbyInfo
{
    public string Code { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string RegionIp { get; set; } = string.Empty;
    public int RegionPort { get; set; }
    public List<ModSetEntry> ModSet { get; set; } = new();
    public string? HostUserId { get; set; }
    public string Host { get; set; } = "Host";
    public int PlayerCount { get; set; }
    public int MaxPlayers { get; set; } = 15;
}
