namespace AmongLauncher.Installer;

public class GameCopier
{
    private static readonly string[] SkipExtensions = [".pdb"];

    public async Task CopyGameAsync(string sourcePath, string destinationPath, IProgress<int>? progress = null)
    {
        if (!Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException($"Source directory not found: {sourcePath}");

        if (Directory.Exists(destinationPath))
        {
            // Preserve the BepInEx subdirectory (all user mods + configs).
            // Delete everything inside the destination EXCEPT BepInEx/.
            foreach (var entry in Directory.GetFileSystemEntries(destinationPath))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, "BepInEx", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Directory.Exists(entry))
                    Directory.Delete(entry, true);
                else
                    File.Delete(entry);
            }
        }
        else
        {
            Directory.CreateDirectory(destinationPath);
        }

        var allFiles = Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories);
        var files = allFiles
            .Where(f => !SkipExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToArray();
        var totalFiles = files.Length;
        var copiedFiles = 0;

        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(sourcePath, file);
                var destFile = Path.Combine(destinationPath, relativePath);

                var destDir = Path.GetDirectoryName(destFile);
                if (destDir != null && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                File.Copy(file, destFile, true);
                copiedFiles++;

                if (totalFiles > 0)
                {
                    var percent = (int)((double)copiedFiles / totalFiles * 100);
                    progress?.Report(percent);
                }
            }
        });
    }
}
