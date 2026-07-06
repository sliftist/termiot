using System.IO;

namespace Termiot;

// A per-user Start menu shortcut (no admin). Created through the WScript.Shell COM object — the only dependency-free way to write .lnk files.
public static class StartMenu
{
    public static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Termiot.lnk");

    public static string? GetCurrentTarget()
    {
        try
        {
            if (!File.Exists(ShortcutPath))
            {
                return null;
            }
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            return (string)shortcut.TargetPath;
        }
        catch (Exception ex)
        {
            AppLog.Write("startmenu", "shortcut read failed: " + ex.Message);
            return null;
        }
    }

    public static void PointAtStableLauncher()
    {
        var exe = Environment.ProcessPath!;
        StableLauncher.EnsureNewest();
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(ShortcutPath);
        // Targets the version-stable bat (minimized so the console flash lands in the taskbar); the exe provides the icon.
        shortcut.TargetPath = StableLauncher.BatPath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
        shortcut.IconLocation = exe + ",0";
        shortcut.WindowStyle = 7;
        shortcut.Description = "Termiot terminal";
        shortcut.Save();
        AppLog.Write("startmenu", "shortcut points at " + StableLauncher.BatPath);
    }
}
