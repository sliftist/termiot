using System.IO;
using System.Text.RegularExpressions;

namespace Termiot;

// The version-stable entry point: %LOCALAPPDATA%\Termiot\termiot.bat forwards to whatever exe it currently names. All external registrations point at the bat, builds repoint it (scripts/stable-launcher.js), and EnsureNewest self-heals it at startup — so nothing ever references a stale instance folder.
public static class StableLauncher
{
    public static string BatPath => Path.Combine(AppPaths.Root, "termiot.bat");

    private static readonly Regex TargetRegex = new("^start \"\" \"([^\"]+)\"", RegexOptions.Compiled);

    public static string? CurrentTarget()
    {
        try
        {
            if (!File.Exists(BatPath))
            {
                return null;
            }
            foreach (var line in File.ReadAllLines(BatPath))
            {
                var match = TargetRegex.Match(line.Trim());
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
        }
        catch
        {
        }
        return null;
    }

    public static void WriteForThisExe()
    {
        var exe = Environment.ProcessPath!;
        File.WriteAllText(BatPath, $"@echo off\r\nstart \"\" \"{exe}\" %*\r\n");
        AppLog.Write("ui", "stable launcher -> " + exe);
    }

    // Newest build wins: repoint only when the bat is missing/broken or this exe is newer than its current target, so an old lingering instance can't downgrade it.
    public static void EnsureNewest()
    {
        try
        {
            var exe = Environment.ProcessPath!;
            var target = CurrentTarget();
            if (target != null && string.Equals(target, exe, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (target == null || !File.Exists(target) || File.GetLastWriteTimeUtc(exe) > File.GetLastWriteTimeUtc(target))
            {
                WriteForThisExe();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", "stable launcher update failed: " + ex.Message);
        }
    }
}
