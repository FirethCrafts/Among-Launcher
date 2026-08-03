namespace AmongApi.Models;

public class SyncResult
{
    public List<ModEntry> Downloaded { get; set; } = [];
    public List<ModEntry> UpToDate { get; set; } = [];
    public List<ModEntry> Failed { get; set; } = [];
}
