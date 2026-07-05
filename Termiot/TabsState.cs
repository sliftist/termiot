using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Termiot;

public sealed class TabInfo
{
    public string Id { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string Title { get; set; } = "cmd";
}

// Per-shell metadata stored inside the shell's own folder (shells\<id>\shell.json), written by the window that watches it.
public sealed class ShellInfo
{
    public string Cwd { get; set; } = "";
    public string Title { get; set; } = "cmd";

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
            var json = JsonSerializer.Serialize(new ShellInfo { Cwd = info.Cwd, Title = info.Title }, new JsonSerializerOptions { WriteIndented = true });
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
    public int OwnerPid { get; set; }
    public long OwnerStartTicks { get; set; }

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

    public List<TabInfo> LoadTabs()
    {
        var tabs = new List<TabInfo>();
        foreach (var id in Shells)
        {
            if (!Directory.Exists(AppPaths.ShellDir(id)))
            {
                continue;
            }
            var info = ShellInfo.Load(id) ?? new ShellInfo();
            tabs.Add(new TabInfo { Id = id, Cwd = info.Cwd, Title = info.Title });
        }
        return tabs;
    }
}
