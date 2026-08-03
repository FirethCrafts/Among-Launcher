namespace AmongLauncher.Models;

public class PresetMod
{
    public string Name { get; set; }
    public string Description { get; set; }
    public string Repo { get; set; }
    public string? PreferredAsset { get; set; }

    public PresetMod(string name, string description, string repo, string? preferredAsset = null)
    {
        Name = name;
        Description = description;
        Repo = repo;
        PreferredAsset = preferredAsset;
    }
}
