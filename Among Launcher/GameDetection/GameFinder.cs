namespace AmongLauncher.GameDetection;

public static class GameFinder
{
    private const string AmongUsExe = "Among Us.exe";
    private const string AmongUsFolder = "Among Us";

    public static GameSearchResult FindAmongUsForStorefront(Storefront? storefront)
    {
        switch (storefront)
        {
            case Storefront.Steam:
                var steam = FindAmongUsSteam();
                return steam == null
                    ? new GameSearchResult()
                    : new GameSearchResult { Path = steam, Storefront = Storefront.Steam };

            case Storefront.Epic:
                return FindAmongUsEpic();

            case Storefront.MicrosoftStore:
                return FindAmongUsXbox();

            default:
                return FindAmongUsWithStorefront();
        }
    }

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
            var manifestsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");

            if (Directory.Exists(manifestsDir))
            {
                var manifestFound = false;
                foreach (var manifestFile in Directory.GetFiles(manifestsDir, "*.item"))
                {
                    try
                    {
                        var json = File.ReadAllText(manifestFile);
                        var item = System.Text.Json.JsonDocument.Parse(json).RootElement;

                        var displayName = GetString(item, "DisplayName");
                        var installLocation = GetString(item, "InstallLocation");

                        var matchesName = displayName != null &&
                            displayName.Equals("Among Us", StringComparison.OrdinalIgnoreCase);
                        var matchesPath = installLocation != null &&
                            installLocation.EndsWith("Among Us", StringComparison.OrdinalIgnoreCase);

                        if (matchesName || matchesPath)
                        {
                            manifestFound = true;
                            if (installLocation != null)
                            {
                                var direct = Path.Combine(installLocation, AmongUsExe);
                                if (File.Exists(direct))
                                {
                                    return new GameSearchResult
                                    {
                                        Path = installLocation,
                                        Storefront = Storefront.Epic
                                    };
                                }

                                var nested = Path.Combine(installLocation, AmongUsFolder, AmongUsExe);
                                if (File.Exists(nested))
                                {
                                    return new GameSearchResult
                                    {
                                        Path = Path.Combine(installLocation, AmongUsFolder),
                                        Storefront = Storefront.Epic
                                    };
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Corrupted .item file — skip and continue.
                    }
                }

                if (manifestFound)
                {
                    return new GameSearchResult { Storefront = Storefront.Epic, DetectedButUnavailable = true };
                }
            }
        }
        catch
        {
            // Manifests dir unreadable — fall through to secondary checks.
        }

        // Existing secondary checks: GameUserSettings.ini DefaultInstallLocation, then fallback paths.
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Epic", "EpicGamesLauncher", "Saved", "Config", "Windows", "GameUserSettings.ini");

            if (File.Exists(configPath))
            {
                var lines = File.ReadAllLines(configPath);
                foreach (var line in lines)
                {
                    if (!line.StartsWith("DefaultInstallLocation=", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var installDir = line.Substring("DefaultInstallLocation=".Length).Trim().Trim('"');
                    if (string.IsNullOrEmpty(installDir)) continue;

                    var gamePath = Path.Combine(installDir, AmongUsFolder, AmongUsExe);
                    if (File.Exists(gamePath))
                    {
                        return new GameSearchResult
                        {
                            Path = Path.Combine(installDir, AmongUsFolder),
                            Storefront = Storefront.Epic
                        };
                    }
                }
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
            {
                return new GameSearchResult { Path = path, Storefront = Storefront.Epic };
            }
        }

        return new GameSearchResult();
    }

    private static string? GetString(System.Text.Json.JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
            return value.GetString();
        return null;
    }

    private static GameSearchResult FindAmongUsXbox()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

            var root = drive.RootDirectory.FullName;

            foreach (var gameFolder in new[] { "Among Us", "AmongUs" })
            {
                var candidates = new[]
                {
                    Path.Combine(root, gameFolder, AmongUsExe),
                    Path.Combine(root, gameFolder, "Content", AmongUsExe),
                    Path.Combine(root, "XboxGames", gameFolder, "Content", AmongUsExe)
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        return new GameSearchResult
                        {
                            Path = Path.GetDirectoryName(candidate),
                            Storefront = Storefront.MicrosoftStore
                        };
                    }
                }
            }
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
                    {
                        return new GameSearchResult
                        {
                            Path = dir,
                            Storefront = Storefront.MicrosoftStore
                        };
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (IsAmongUsInstalledFromMsStore())
            {
                return new GameSearchResult { Storefront = Storefront.MicrosoftStore, DetectedButUnavailable = true };
            }
        }
        catch { }

        try
        {
            var packagesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages");

            if (Directory.Exists(packagesDir))
            {
                var amongPackages = Directory.GetDirectories(packagesDir, "InnerSloth.LLC-*");
                foreach (var pkg in amongPackages.OrderByDescending(d => new DirectoryInfo(d).LastWriteTime))
                {
                    var gamePath = Path.Combine(pkg, "LocalCache", "Local", "Microsoft", "WindowsApps", AmongUsExe);
                    if (File.Exists(gamePath))
                    {
                        return new GameSearchResult
                        {
                            Path = Path.GetDirectoryName(gamePath),
                            Storefront = Storefront.MicrosoftStore
                        };
                    }
                }
            }
        }
        catch { }

        return new GameSearchResult();
    }

    public static bool IsAmongUsInstalledFromMsStore()
    {
        try
        {
            var packagesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages");

            return Directory.Exists(packagesDir) &&
                Directory.GetDirectories(packagesDir, "InnerSloth.LLC-*").Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
