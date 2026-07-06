using Microsoft.Win32;

namespace Termiot;

// "Open in Termiot" in Explorer's right-click menu for folders, folder backgrounds, and files — per-user (HKCU\Software\Classes), no elevation. Explorer passes the clicked path via --open; files resolve to their containing folder.
public static class ExplorerContextMenu
{
    private static readonly string[] KeyPaths =
    {
        @"Software\Classes\Directory\shell\Termiot",
        @"Software\Classes\Directory\Background\shell\Termiot",
        @"Software\Classes\*\shell\Termiot",
    };

    public static string? GetCurrentExe()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPaths[0] + @"\command");
            if (key?.GetValue("") is not string command || command.Length == 0)
            {
                return null;
            }
            int quote = command.IndexOf('"', 1);
            return command.StartsWith('"') && quote > 0 ? command[1..quote] : command.Split(' ')[0];
        }
        catch
        {
            return null;
        }
    }

    public static void Install()
    {
        var exe = Environment.ProcessPath!;
        StableLauncher.EnsureNewest();
        foreach (var path in KeyPaths)
        {
            // Folder backgrounds only provide %V (there is no clicked item); items use %1. The command runs the version-stable bat; only the icon references a concrete exe.
            string arg = path.Contains("Background") ? "%V" : "%1";
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue("", "Open in Termiot");
            key.SetValue("Icon", $"\"{exe}\",0");
            using var command = key.CreateSubKey("command");
            command.SetValue("", $"\"{StableLauncher.BatPath}\" --open \"{arg}\"");
        }
        AppLog.Write("explorer", "context menu registered via " + StableLauncher.BatPath);
    }

    public static void Remove()
    {
        foreach (var path in KeyPaths)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(path, false);
            }
            catch
            {
            }
        }
        AppLog.Write("explorer", "context menu removed");
    }
}
