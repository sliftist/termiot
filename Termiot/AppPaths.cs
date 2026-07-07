using System.IO;

namespace Termiot;

// On-disk layout: %LOCALAPPDATA%\Termiot\shells\<shellId>\ is the complete state of one shell instance — output.log (raw persisted terminal stream), shell.json (cwd/title, written by the window), host.json (host pid + process start time, written by the host, used for liveness checks that survive pid reuse). windows\<windowId>.json is one window: which shells it contains, where the window sits on screen, and the owning process (pid + start time). No file handles are held anywhere — everything is open-read/write-close.
public static class AppPaths
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Termiot");
    public static readonly string ShellsDir = Path.Combine(Root, "shells");
    public static readonly string WindowsDir = Path.Combine(Root, "windows");
    public static readonly string SettingsFile = Path.Combine(Root, "settings.json");
    public static readonly string AppLogFile = Path.Combine(Root, "app.log");
    // Output of the most recent rebuild-button run — overwritten each click so it's always the latest, surfaced in the reload button's tooltip and the settings window.
    public static readonly string BuildLogFile = Path.Combine(Root, "build.log");
    public static readonly string UnhandledEscapesFile = Path.Combine(Root, "unhandled-escapes.md");

    static AppPaths()
    {
        Directory.CreateDirectory(ShellsDir);
        Directory.CreateDirectory(WindowsDir);
    }

    public static string ShellDir(string shellId) => Path.Combine(ShellsDir, shellId);

    // The shell output is kept in two rotating files: output.log is the active file being appended; when it exceeds the cap it's rotated to output.1.log (the older one is discarded), so total retained history is bounded. Replay reads output.1.log then output.log in order.
    public static string LogFile(string shellId) => Path.Combine(ShellDir(shellId), "output.log");

    public static string PrevLogFile(string shellId) => Path.Combine(ShellDir(shellId), "output.1.log");

    public static string ShellInfoFile(string shellId) => Path.Combine(ShellDir(shellId), "shell.json");

    public static string HostInfoFile(string shellId) => Path.Combine(ShellDir(shellId), "host.json");

    public static string ShellHistoryFile(string shellId) => Path.Combine(ShellDir(shellId), "history.txt");

    // Optional, user-authored: a batch script executed (by sending its path to the shell) when the shell is resumed. The shell's SHELLFOLDER env var points at its folder so scripts can find it.
    public static string AutoResumeFile(string shellId) => Path.Combine(ShellDir(shellId), "AUTORESUME.cmd");

    public static string WindowFile(string windowId) => Path.Combine(WindowsDir, windowId + ".json");

    public static string PipeName(string shellId) => "termiot-shell-" + shellId;
}
