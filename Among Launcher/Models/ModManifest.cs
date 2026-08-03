namespace AmongLauncher.Models;

public class ModManifest
{
    public int Schema { get; set; }
    public List<ModManifestEntry> Mods { get; set; } = [];
}

public class ModManifestEntry
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
}
