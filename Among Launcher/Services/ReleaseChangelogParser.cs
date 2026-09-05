namespace AmongLauncher.Services;

public static class ReleaseChangelogParser
{
    // Returns (launcherChangelog, modChangelog) — each is trimmed string or null if section not found/empty.
    public static (string? Launcher, string? Mod) Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        var lines = body.Replace("\r\n", "\n").Split('\n');
        int launcherStart = -1, modStart = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim().TrimStart('#').Trim();
            if (launcherStart == -1 && t.StartsWith("launcher", StringComparison.OrdinalIgnoreCase))
                launcherStart = i;
            else if (modStart == -1 && t.StartsWith("mod", StringComparison.OrdinalIgnoreCase))
                modStart = i;
        }
        // If neither header found, no structured changelog
        if (launcherStart == -1 && modStart == -1) return (null, null);

        string? Extract(int start, int endExclusive)
        {
            if (start == -1) return null;
            int end = endExclusive == -1 ? lines.Length : endExclusive;
            var slice = string.Join("\n", lines[(start+1)..end]).Trim();
            return string.IsNullOrWhiteSpace(slice) ? null : slice;
        }

        // Determine ordering
        string? launcher = null, mod = null;
        if (launcherStart != -1 && modStart != -1)
        {
            if (launcherStart < modStart) { launcher = Extract(launcherStart, modStart); mod = Extract(modStart, -1); }
            else { mod = Extract(modStart, launcherStart); launcher = Extract(launcherStart, -1); }
        }
        else if (launcherStart != -1) launcher = Extract(launcherStart, -1);
        else if (modStart != -1) mod = Extract(modStart, -1);

        return (launcher, mod);
    }
}
