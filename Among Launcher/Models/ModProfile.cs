namespace AmongLauncher.Models;

public class ModProfile
{
    public string Name { get; set; } = string.Empty;
    public List<ModSetEntry> Mods { get; set; } = new();
}
