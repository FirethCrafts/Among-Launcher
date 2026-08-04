namespace AmongLauncher.Services.Lobby;

public static class LobbyTypeDetector
{
    private static readonly HashSet<string> ExcludedDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "AmongApi.dll",
        "0Harmony.dll", "AsmResolver.dll", "BepInEx.Core.dll",
        "BepInEx.Preloader.Core.dll", "BepInEx.Unity.Common.dll",
        "BepInEx.Unity.IL2CPP.dll"
    };

    public static string DetectLobbyType(string moddedPath)
    {
        var pluginsDir = Path.Combine(moddedPath, "BepInEx", "plugins");
        if (!Directory.Exists(pluginsDir)) return "vanilla";

        var hasMod = Directory.EnumerateFiles(pluginsDir, "*.dll")
            .Any(f => !ExcludedDlls.Contains(Path.GetFileName(f)));
        return hasMod ? "modded" : "vanilla";
    }
}
