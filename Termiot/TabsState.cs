using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Termiot;

public sealed class TabInfo
{
    public string Id { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string Title { get; set; } = "cmd";
    public string ForcedTitle { get; set; } = "";
    // Typed-but-unsent input, persisted so reloads/takeovers don't lose it.
    public string PendingInput { get; set; } = "";
    // Sort key from --ensure --order: keeps scripted tabs in a consistent position; the highest-order tab wins focus. 0 = unordered (sorts first).
    public int EnsureOrder { get; set; }
    // When the watching window saw the shell process die; 0 while running (or if it died unwatched). Compared against the window's close time to decide whether a dead shell was part of the working set worth restoring.
    public long ExitedAtTicks { get; set; }
    // Sticky preference: this shell's host should be spawned elevated (prompts UAC). The renderer stays unelevated.
    public bool Elevated { get; set; }
    // Persisted scroll position: -1 = pinned to the bottom (follows live output), else lines up from the bottom.
    public int ScrollFromBottom { get; set; } = -1;
    // The app enabled win32-input-mode (?9001); restored on resume so raw-key encoding is correct before the old scrollback (which holds the startup enable) finishes parsing.
    public bool Win32Input { get; set; }
}

// Per-shell metadata stored inside the shell's own folder (shells\<id>\shell.json), written by the window that watches it.
public sealed class ShellInfo
{
    public string Cwd { get; set; } = "";
    public string Title { get; set; } = "cmd";
    // A user-renamed tab keeps this title permanently (over both the automatic folder+command title and process-set titles) until cleared.
    public string ForcedTitle { get; set; } = "";
    public string PendingInput { get; set; } = "";
    public int EnsureOrder { get; set; }
    public long ExitedAtTicks { get; set; }
    public bool Elevated { get; set; }
    public int ScrollFromBottom { get; set; } = -1;
    public bool Win32Input { get; set; }

    public static ShellInfo? Load(string shellId)
    {
        try
        {
            var path = AppPaths.ShellInfoFile(shellId);
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<ShellInfo>(File.ReadAllText(path));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("state", $"shell.json load failed for {shellId}: {ex.Message}");
        }
        return null;
    }

    public static void Save(TabInfo info)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ShellDir(info.Id));
            var json = JsonSerializer.Serialize(new ShellInfo { Cwd = info.Cwd, Title = info.Title, ForcedTitle = info.ForcedTitle, PendingInput = info.PendingInput, EnsureOrder = info.EnsureOrder, ExitedAtTicks = info.ExitedAtTicks, Elevated = info.Elevated, ScrollFromBottom = info.ScrollFromBottom, Win32Input = info.Win32Input }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.ShellInfoFile(info.Id), json);
        }
        catch (Exception ex)
        {
            AppLog.Write("state", $"shell.json save failed for {info.Id}: {ex.Message}");
        }
    }
}

// Written by the shell host at startup. The start time disambiguates recycled pids: a pid match alone does not prove the host is alive.
public sealed class HostInfo
{
    public int Pid { get; set; }
    public long StartTicks { get; set; }

    public static void SaveCurrentProcess(string shellId)
    {
        try
        {
            var self = Process.GetCurrentProcess();
            var json = JsonSerializer.Serialize(new HostInfo { Pid = self.Id, StartTicks = self.StartTime.Ticks });
            File.WriteAllText(AppPaths.HostInfoFile(shellId), json);
        }
        catch (Exception ex)
        {
            AppLog.Write("state", $"host.json save failed for {shellId}: {ex.Message}");
        }
    }

    public static bool IsShellAlive(string shellId)
    {
        try
        {
            var path = AppPaths.HostInfoFile(shellId);
            if (!File.Exists(path))
            {
                return false;
            }
            var info = JsonSerializer.Deserialize<HostInfo>(File.ReadAllText(path));
            if (info == null)
            {
                return false;
            }
            return ProcessAlive(info.Pid, info.StartTicks);
        }
        catch
        {
            return false;
        }
    }

    public static bool ProcessAlive(int pid, long startTicks)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime.Ticks == startTicks && !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    // Forcefully and immediately kill the shell host (and its ConPTY/shell children) by the recorded pid — no graceful pipe message that a busy host could block on. Thread-safe and non-blocking; the log is append-on-write, so a kill loses at most the last unflushed instant of scrollback, same as any crash.
    public static void Kill(string shellId)
    {
        try
        {
            var path = AppPaths.HostInfoFile(shellId);
            if (!File.Exists(path))
            {
                return;
            }
            var info = JsonSerializer.Deserialize<HostInfo>(File.ReadAllText(path));
            if (info != null && ProcessAlive(info.Pid, info.StartTicks))
            {
                using var p = Process.GetProcessById(info.Pid);
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("state", $"host kill failed for {shellId}: {ex.Message}");
        }
    }
}

// windows\<id>.json — the window process's own state: which shells it renders, in order, where the window is on screen, and which process currently owns it (pid + start time, same liveness scheme as shells — no lock handles are ever held).
public sealed class WindowState
{
    public List<string> Shells { get; set; } = new();
    public int ActiveIndex { get; set; }
    // Nullable because WPF reports NaN/Infinity for not-yet-shown or closing windows, and System.Text.Json refuses to serialize those — null means "unset".
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    // User-assigned window name; also the lookup key for --ensure targeted launches.
    public string Name { get; set; } = "";
    public int OwnerPid { get; set; }
    public long OwnerStartTicks { get; set; }
    // True only when the user deliberately closed the window (X button). Crashes never write it, and session-ending (shutdown/logoff) closes deliberately leave it false — both cases reopen on the next launch.
    public bool ClosedCleanly { get; set; }
    // Stamped on any window close (clean or session-ending); 0 while running or after a crash. Paired with each shell's ExitedAtTicks to tell dead tabs the user had abandoned apart from shells that died with the window.
    public long ClosedAtTicks { get; set; }

    public static WindowState Load(string windowId)
    {
        try
        {
            var path = AppPaths.WindowFile(windowId);
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<WindowState>(File.ReadAllText(path)) ?? new WindowState();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("state", $"window load failed for {windowId}: {ex.Message}");
        }
        return new WindowState();
    }

    public void Save(string windowId)
    {
        try
        {
            File.WriteAllText(AppPaths.WindowFile(windowId), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Write("state", $"window save failed for {windowId}: {ex.Message}");
        }
    }

    public static void Delete(string windowId)
    {
        try
        {
            File.Delete(AppPaths.WindowFile(windowId));
        }
        catch
        {
        }
    }

    // A shell that died more than this long before the window closed was a dead tab the user had abandoned, not part of the working set — anything newer (or that died after the close, or was never seen dying) was plausibly alive when the window went away.
    public const int ExitNearCloseGraceSeconds = 60;

    public List<TabInfo> LoadTabs()
    {
        var tabs = new List<TabInfo>();
        long exitCutoff = ClosedAtTicks > 0 ? ClosedAtTicks - TimeSpan.FromSeconds(ExitNearCloseGraceSeconds).Ticks : 0;
        foreach (var id in Shells)
        {
            if (!Directory.Exists(AppPaths.ShellDir(id)))
            {
                continue;
            }
            var info = ShellInfo.Load(id) ?? new ShellInfo();
            if (exitCutoff > 0 && info.ExitedAtTicks != 0 && info.ExitedAtTicks < exitCutoff && !HostInfo.IsShellAlive(id))
            {
                continue;
            }
            tabs.Add(new TabInfo { Id = id, Cwd = info.Cwd, Title = info.Title, ForcedTitle = info.ForcedTitle, PendingInput = info.PendingInput, EnsureOrder = info.EnsureOrder, ExitedAtTicks = info.ExitedAtTicks, Elevated = info.Elevated, ScrollFromBottom = info.ScrollFromBottom, Win32Input = info.Win32Input });
        }
        return tabs;
    }
}
