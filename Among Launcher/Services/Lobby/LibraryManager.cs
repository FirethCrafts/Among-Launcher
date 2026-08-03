using AmongLauncher.Config;
using AmongLauncher.Models;

namespace AmongLauncher.Services.Lobby;

/// <summary>
/// Manages the mod library: a persistent folder of DLLs outside BepInEx/plugins
/// that can be moved into a profile or installed to the game.
/// </summary>
public class LibraryManager
{
    private readonly LauncherConfig _config;
    private readonly string _libraryDir;

    public string LibraryDir => _libraryDir;

    public LibraryManager(LauncherConfig config)
    {
        _config = config;
        _libraryDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmongLauncher", "Library");
    }

    public List<LibraryEntry> LoadLibrary()
    {
        // Prune entries whose files no longer exist on disk
        _config.Library.RemoveAll(e =>
            !string.IsNullOrEmpty(e.FileName) &&
            !File.Exists(Path.Combine(_libraryDir, e.FileName)));
        return _config.Library;
    }

    public bool IsInLibrary(string fileName)
    {
        var path = Path.Combine(_libraryDir, fileName);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    /// <summary>Copies a DLL into the library and records it. Does NOT delete the source.</summary>
    public bool AddToLibrary(string sourceFilePath, string? downloadUrl = null, string? version = null)
    {
        if (!File.Exists(sourceFilePath)) return false;

        Directory.CreateDirectory(_libraryDir);
        var fileName = Path.GetFileName(sourceFilePath);
        var dest = Path.Combine(_libraryDir, fileName);
        File.Copy(sourceFilePath, dest, overwrite: true);

        var existing = _config.Library.FirstOrDefault(e => e.FileName == fileName);
        if (existing != null)
        {
            if (!string.IsNullOrEmpty(downloadUrl)) existing.DownloadUrl = downloadUrl;
            if (!string.IsNullOrEmpty(version)) existing.Version = version;
        }
        else
        {
            _config.Library.Add(new LibraryEntry
            {
                FileName = fileName,
                DownloadUrl = downloadUrl ?? string.Empty,
                Version = version
            });
        }

        _config.Save();
        return true;
    }

    /// <summary>Moves a DLL out of a source folder into the library (deletes the source copy).</summary>
    public bool MoveToLibrary(string sourceFilePath, string? downloadUrl = null, string? version = null)
    {
        if (AddToLibrary(sourceFilePath, downloadUrl, version))
        {
            try { File.Delete(sourceFilePath); } catch { /* best effort */ }
            return true;
        }
        return false;
    }

    /// <summary>Copies a library DLL into the game's plugins folder.</summary>
    public bool InstallToPlugins(string fileName, string pluginsDir)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var src = Path.Combine(_libraryDir, fileName);
        if (!File.Exists(src)) return false;

        Directory.CreateDirectory(pluginsDir);
        File.Copy(src, Path.Combine(pluginsDir, fileName), overwrite: true);
        return true;
    }

    /// <summary>Deletes a mod from the library (file + registry).</summary>
    public bool RemoveFromLibrary(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;

        var path = Path.Combine(_libraryDir, fileName);
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }

        var removed = _config.Library.RemoveAll(e => e.FileName == fileName) > 0;
        _config.Save();
        return removed || !File.Exists(path);
    }

    /// <summary>
    /// Moves every DLL in pluginsDir that is not in <paramref name="keepFileNames"/>
    /// into the library. Files already in the library are just removed from plugins.
    /// </summary>
    public List<string> MoveNonListedToLibrary(string pluginsDir, ICollection<string> keepFileNames)
    {
        Directory.CreateDirectory(_libraryDir);
        var moved = new List<string>();

        if (!Directory.Exists(pluginsDir)) return moved;

        foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll"))
        {
            var fileName = Path.GetFileName(dll);
            if (keepFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                continue;

            // If it's not already in the library, copy it in first.
            if (!IsInLibrary(fileName))
                AddToLibrary(dll);

            try { File.Delete(dll); } catch { /* locked; leave in plugins */ }
            moved.Add(fileName);
        }

        _config.Save();
        return moved;
    }
}
