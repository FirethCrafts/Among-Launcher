using AmongLauncher.Services;

namespace AmongLauncher.Services.Lobby;

/// <summary>
/// Removes mods that are neither in the persistent whitelist nor required by the
/// lobby. Unapproved mods are quarantined to <c>BepInEx/plugins/.disabled</c> (never
/// hard-deleted) so nothing is lost while keeping the game state clean.
/// Comparison is case-insensitive to match Windows file semantics.
/// </summary>
public class ModCleanupEngine
{
    /// <summary>
    /// Persistent client-side mods that must never be quarantined. Includes the
    /// IPC helper itself and common essential mods. File and folder names are
    /// matched case-insensitively; a name here protects any matching file or folder.
    /// </summary>
    private static readonly IReadOnlySet<string> Whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AmongApi.dll",
        "AUnlocker.dll",
        "helper_mod.dll",
        "aunlocker"
    };

    private readonly string _pluginsDir;
    private readonly string _disabledDir;

    public ModCleanupEngine(string pluginsDir)
    {
        _pluginsDir = pluginsDir;
        _disabledDir = Path.Combine(pluginsDir, ".disabled");
    }

    /// <summary>
    /// Quarantines every file/folder in the plugins directory that is not in the
    /// whitelist and not among the lobby's required mods. Returns the list of
    /// quarantined items. The <c>.disabled</c> folder itself is always skipped.
    /// </summary>
    public Task<IReadOnlyList<string>> QuarantineAsync(
        IReadOnlyCollection<string> requiredFileNames,
        CancellationToken ct)
    {
        var quarantined = new List<string>();

        if (!Directory.Exists(_pluginsDir))
            return Task.FromResult<IReadOnlyList<string>>(quarantined);

        var required = new HashSet<string>(requiredFileNames, StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(_disabledDir);

        foreach (var entry in Directory.GetFileSystemEntries(_pluginsDir))
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry);
            if (IsDisabledDir(name) || Whitelist.Contains(name) || required.Contains(name))
                continue;

            // Only quarantine DLL files and directories; leave companion files
            // (e.g. *.deps.json) untouched so required mods keep their metadata.
            var isDll = !Directory.Exists(entry) &&
                        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            if (!Directory.Exists(entry) && !isDll)
                continue;

            try
            {
                var dest = Path.Combine(_disabledDir, name);
                if (Directory.Exists(entry))
                    Directory.Move(entry, dest);
                else
                    File.Move(entry, dest);

                quarantined.Add(name);
                LauncherLog.Write($"[ModCleanup] Quarantined unapproved mod: {name}");
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"[ModCleanup] Failed to quarantine {name}: {ex.Message}");
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(quarantined);
    }

    private static bool IsDisabledDir(string name) =>
        string.Equals(name, ".disabled", StringComparison.OrdinalIgnoreCase);
}
