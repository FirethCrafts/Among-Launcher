namespace AmongLauncher.Installer;

public class BepInExInstaller
{
    private static readonly string BepInExSourceDir = Path.GetFullPath(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "BepInEx"));

    public Task InstallAsync(string gamePath, IProgress<int>? progress = null)
    {
        if (!Directory.Exists(BepInExSourceDir))
            throw new DirectoryNotFoundException($"BepInEx source not found: {BepInExSourceDir}");

        progress?.Report(0);

        var files = Directory.GetFiles(BepInExSourceDir, "*.*", SearchOption.AllDirectories);
        var totalFiles = files.Length;
        var copiedFiles = 0;

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(BepInExSourceDir, file);
            var destPath = Path.Combine(gamePath, relativePath);
            var destDir = Path.GetDirectoryName(destPath);

            if (destDir != null && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            File.Copy(file, destPath, true);
            copiedFiles++;

            if (totalFiles > 0)
            {
                var percent = (int)((double)copiedFiles / totalFiles * 100);
                progress?.Report(percent);
            }
        }

        progress?.Report(100);
        return Task.CompletedTask;
    }
}
