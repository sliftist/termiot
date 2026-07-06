using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Termiot;

// Cursor's Ctrl+Shift+C (openNativeConsole) launches the exe named in terminal.external.windowsExec with the workspace as working directory. Edits are done textually with a regex instead of JSON round-tripping because settings.json is JSONC — comments and trailing commas must survive.
public static class CursorIntegration
{
    private const string SettingKey = "terminal.external.windowsExec";
    private static readonly Regex SettingRegex = new("(\"terminal\\.external\\.windowsExec\"\\s*:\\s*\")((?:[^\"\\\\]|\\\\.)*)(\")", RegexOptions.Compiled);

    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "settings.json");

    public static bool CursorInstalled => Directory.Exists(Path.GetDirectoryName(SettingsPath)!);

    public static string? GetCurrent()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.TryGetProperty(SettingKey, out var value) ? value.GetString() : null;
        }
        catch (Exception ex)
        {
            AppLog.Write("cursor", "settings read failed: " + ex.Message);
            return null;
        }
    }

    // Registers the version-stable bat, never a specific instance exe — builds repoint the bat, so this registration never goes stale.
    public static void SetToStableLauncher()
    {
        StableLauncher.EnsureNewest();
        Set(StableLauncher.BatPath);
    }

    private static void Set(string exe)
    {
        var escaped = JsonEncodedText.Encode(exe).ToString();
        string content = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : "{\n}";
        if (SettingRegex.IsMatch(content))
        {
            content = SettingRegex.Replace(content, m => m.Groups[1].Value + escaped + m.Groups[3].Value, 1);
        }
        else
        {
            int brace = content.IndexOf('{');
            if (brace < 0)
            {
                content = "{\n}";
                brace = 0;
            }
            content = content[..(brace + 1)] + $"\n    \"{SettingKey}\": \"{escaped}\"," + content[(brace + 1)..];
        }
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, content);
        AppLog.Write("cursor", $"windowsExec set to {exe}");
    }
}
