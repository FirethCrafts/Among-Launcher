using System.IO.Compression;
using System.Reflection;

namespace AmongLauncher.Installer;

public class BepInExInstaller
{
    public async Task InstallAsync(string gamePath, IProgress<int>? progress = null)
    {
        progress?.Report(0);

        // Extract BepInEx from embedded resource
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "AmongLauncher.Resources.BepInEx.zip";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new FileNotFoundException("BepInEx resource not found in assembly.");

        var tempZip = Path.Combine(Path.GetTempPath(), "BepInEx.zip");

        try
        {
            // Write stream to temp file
            await using (var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.CopyToAsync(fileStream);
            }

            progress?.Report(50);

            // Extract to game folder
            await Task.Run(() =>
            {
                ZipFile.ExtractToDirectory(tempZip, gamePath, overwriteFiles: true);
            });

            progress?.Report(100);
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
    }
}
