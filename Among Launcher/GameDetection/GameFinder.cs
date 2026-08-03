namespace AmongLauncher.GameDetection;

public static class GameFinder
{
    private const string AmongUsExe = "Among Us.exe";
    private const string AmongUsFolder = "Among Us";

    public static string? FindAmongUs() => FindAmongUsWithStorefront().Path;

    public static GameSearchResult FindAmongUsWithStorefront()
    {
        var steam = FindAmongUsSteam();
        if (steam != null) return new GameSearchResult { Path = steam, Storefront = Storefront.Steam };

        var epic = FindAmongUsEpic();
        if (epic.Path != null) return epic;

        var xbox = FindAmongUsXbox();
        if (xbox.Path != null) return xbox;

        return epic.DetectedButUnavailable
            ? epic
            : xbox.DetectedButUnavailable
                ? xbox
                : new GameSearchResult();
    }

    private static string? FindAmongUsSteam()
    {
        var steamFinder = new SteamFinder();
        var libraries = steamFinder.FindSteamLibraries();

        foreach (var library in libraries)
        {
            var gamePath = Path.Combine(library, "steamapps", "common", AmongUsFolder, AmongUsExe);
            if (File.Exists(gamePath))
                return Path.GetDirectoryName(gamePath);
        }

        var fallbackPaths = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Among Us",
            @"C:\Program Files\Steam\steamapps\common\Among Us",
            @"D:\SteamLibrary\steamapps\common\Among Us",
            @"D:\Steam\steamapps\common\Among Us"
        };

        foreach (var path in fallbackPaths)
        {
            if (File.Exists(Path.Combine(path, AmongUsExe)))
                return path;
        }

        return null;
    }

    private static GameSearchResult FindAmongUsEpic()
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Epic", "EpicGamesLauncher", "Saved", "Config", "Windows", "GameUserSettings.ini");

            if (!File.Exists(configPath)) return new GameSearchResult();

            var lines = File.ReadAllLines(configPath);
            foreach (var line in lines)
            {
                if (!line.StartsWith("DefaultInstallLocation=", StringComparison.OrdinalIgnoreCase))
                    continue;

                var installDir = line.Substring("DefaultInstallLocation=".Length).Trim().Trim('"');
                if (string.IsNullOrEmpty(installDir)) continue;

                var gamePath = Path.Combine(installDir, AmongUsFolder, AmongUsExe);
                if (File.Exists(gamePath))
                    return new GameSearchResult
                    {
                        Path = Path.GetDirectoryName(gamePath),
                        Storefront = Storefront.Epic
                    };
            }
        }
        catch { }

        var epicFallback = new[]
        {
            @"C:\Program Files\Epic Games\Among Us",
            @"D:\Epic Games\Among Us",
            @"E:\Epic Games\Among Us"
        };

        foreach (var path in epicFallback)
        {
            if (File.Exists(Path.Combine(path, AmongUsExe)))
                return new GameSearchResult { Path = path, Storefront = Storefront.Epic };
        }

        return new GameSearchResult();
    }

    private static GameSearchResult FindAmongUsXbox()
    {
        var xboxPaths = new[]
        {
            @"C:\XboxGames",
            @"D:\XboxGames",
            @"E:\XboxGames",
            @"F:\XboxGames"
        };

        foreach (var root in xboxPaths)
        {
            var gamePath = Path.Combine(root, AmongUsFolder, AmongUsExe);
            if (File.Exists(gamePath))
                return new GameSearchResult
                {
                    Path = Path.GetDirectoryName(gamePath),
                    Storefront = Storefront.MicrosoftStore
                };
        }

        try
        {
            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps");

            if (Directory.Exists(windowsApps))
            {
                var amongDirs = Directory.GetDirectories(windowsApps, "Innersloth*");
                foreach (var dir in amongDirs.OrderByDescending(d => new DirectoryInfo(d).LastWriteTime))
                {
                    var gamePath = Path.Combine(dir, AmongUsExe);
                    if (File.Exists(gamePath))
                        return new GameSearchResult
                        {
                            Path = dir,
                            Storefront = Storefront.MicrosoftStore
                        };
                }
            }
        }
        catch { }

        return new GameSearchResult();
    }
}
