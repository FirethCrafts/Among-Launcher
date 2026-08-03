namespace AmongLauncher.Models;

public class ModSetEntry
{
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
    public string? Version { get; set; }
}
