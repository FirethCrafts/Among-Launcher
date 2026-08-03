using AmongLauncher.GameDetection;

namespace AmongLauncher.Installer;

public class BepInExInstaller
{
    private static readonly string BepInExSourceDir = Path.GetFullPath(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "BepInEx"));

    private static readonly string BepInExMsEpicSourceDir = Path.GetFullPath(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "bepinex-ms-epic"));

    public Task InstallAsync(string gamePath, Storefront? storefront, IProgress<int>? progress = null)
    {
        // MS Store / Epic use the shared "bepinex-ms-epic" build; Steam uses the default one.
        var sourceDir = storefront is Storefront.Epic or Storefront.MicrosoftStore
            ? BepInExMsEpicSourceDir
            : BepInExSourceDir;

        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"BepInEx source not found: {sourceDir}");

        progress?.Report(0);

        var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
        var totalFiles = files.Length;
        var copiedFiles = 0;

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
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
