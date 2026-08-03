namespace AmongLauncher.Models;

public class LibraryEntry
{
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Sha256 { get; set; }
}
