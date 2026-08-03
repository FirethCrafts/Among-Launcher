namespace AmongLauncher.Steam;

public class AmongUsLocator
{
    private const string AmongUsExe = "Among Us.exe";
    private const string AmongUsFolder = "Among Us";

    public string? FindAmongUs()
    {
        var steamFinder = new SteamFinder();
        var libraries = steamFinder.FindSteamLibraries();

        foreach (var library in libraries)
        {
            var gamePath = Path.Combine(library, "steamapps", "common", AmongUsFolder, AmongUsExe);
            if (File.Exists(gamePath))
            {
                return Path.GetDirectoryName(gamePath);
            }
        }

        // Fallback: check common install locations
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
            {
                return path;
            }
        }

        return null;
    }
}
