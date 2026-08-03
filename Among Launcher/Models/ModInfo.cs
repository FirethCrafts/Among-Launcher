namespace AmongLauncher.Models;

public class ModInfo
{
    public string ModId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public bool RequiresRestart { get; set; }
    public List<string> Dependencies { get; set; } = [];
    public string EntryPoint { get; set; } = string.Empty;
}
