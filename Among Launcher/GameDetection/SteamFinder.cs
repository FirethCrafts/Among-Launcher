using System.Text.RegularExpressions;

namespace AmongLauncher.GameDetection;

public class SteamFinder
{
    private static readonly string[] CommonSteamPaths =
    [
        @"C:\Program Files (x86)\Steam",
        @"C:\Program Files\Steam",
        @"D:\Steam",
        @"D:\SteamLibrary",
        @"E:\Steam",
        @"E:\SteamLibrary",
        @"F:\Steam",
        @"F:\SteamLibrary"
    ];

    public List<string> FindSteamLibraries()
    {
        var libraries = new List<string>();

        // Try registry first
        var steamPath = GetSteamPathFromRegistry();
        if (steamPath != null)
        {
            libraries.Add(steamPath);
        }

        // Parse libraryfolders.vdf from known paths
        foreach (var basePath in CommonSteamPaths)
        {
            if (!Directory.Exists(basePath)) continue;

            var configPath = Path.Combine(basePath, "config", "libraryfolders.vdf");
            if (File.Exists(configPath))
            {
                libraries.AddRange(ParseLibraryFoldersVdf(configPath));
            }
        }

        return libraries.Distinct().ToList();
    }

    private string? GetSteamPathFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Valve\Steam");
            return key?.GetValue("InstallPath") as string;
        }
        catch
        {
            return null;
        }
    }

    private List<string> ParseLibraryFoldersVdf(string vdfPath)
    {
        var paths = new List<string>();

        try
        {
            var content = File.ReadAllText(vdfPath);

            // Match lines like: "path"		"C:\\SteamLibrary"
            var matches = Regex.Matches(content, @"""path""\s+""([^""]+)""");

            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    var path = match.Groups[1].Value.Replace("\\\\", "\\");
                    if (Directory.Exists(path))
                    {
                        paths.Add(path);
                    }
                }
            }
        }
        catch
        {
            // VDF parsing failed, continue
        }

        return paths;
    }
}
