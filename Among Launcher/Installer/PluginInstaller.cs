namespace AmongLauncher.Installer;

public class PluginInstaller
{
    public void InstallPlugin(string moddedPath, string pluginSourcePath)
    {
        var pluginsDir = Path.Combine(moddedPath, "BepInEx", "plugins");

        if (!Directory.Exists(pluginsDir))
        {
            Directory.CreateDirectory(pluginsDir);
        }

        var destPath = Path.Combine(pluginsDir, Path.GetFileName(pluginSourcePath));
        File.Copy(pluginSourcePath, destPath, overwrite: true);
    }

    public void InstallPluginFromBytes(string moddedPath, string fileName, byte[] data)
    {
        var pluginsDir = Path.Combine(moddedPath, "BepInEx", "plugins");

        if (!Directory.Exists(pluginsDir))
        {
            Directory.CreateDirectory(pluginsDir);
        }

        var destPath = Path.Combine(pluginsDir, fileName);
        File.WriteAllBytes(destPath, data);
    }
}
