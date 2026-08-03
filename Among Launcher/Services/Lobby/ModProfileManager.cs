using AmongLauncher.Config;
using AmongLauncher.Models;

namespace AmongLauncher.Services.Lobby;

public class ModProfileManager
{
    private readonly LauncherConfig _config;

    public ModProfileManager(LauncherConfig config) => _config = config;

    public List<ModProfile> LoadProfiles() => _config.Profiles;

    public void SaveProfile(string name, List<ModSetEntry> mods)
    {
        _config.Profiles.RemoveAll(p => p.Name == name);
        _config.Profiles.Add(new ModProfile { Name = name, Mods = mods });
        _config.Save();
    }
}
