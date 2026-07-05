using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Termiot;

public static class Program
{
    // Two processes racing for the same dead window both write their claim; the settle delay lets the last write win before each reads back to see who actually got it.
    private const int ClaimSettleMs = 150;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--selftest")
        {
            AppLog.InstallCrashHandlers(null);
            return SelfTest.Run();
        }
        if (args.Length >= 2 && args[0] == "--shellhost")
        {
            AppLog.InstallCrashHandlers(null);
            try
            {
                ShellHostProc.Run(args[1], args.Length >= 3 ? args[2] : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }
            catch (Exception ex)
            {
                AppLog.Write("shellhost", ex.ToString());
                return 1;
            }
            return 0;
        }

        string? windowId = null;
        if (args.Length >= 2 && args[0] == "--window")
        {
            windowId = args[1];
            if (!TryClaimWindow(windowId))
            {
                return 0;
            }
        }
        else
        {
            var existing = ListWindowIds();
            foreach (var id in existing)
            {
                if (windowId == null && TryClaimWindow(id))
                {
                    windowId = id;
                }
            }
            foreach (var id in existing)
            {
                if (id != windowId)
                {
                    SpawnWindowProcess(id);
                }
            }
            if (windowId == null)
            {
                windowId = NewId();
                TryClaimWindow(windowId);
            }
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        AppLog.InstallCrashHandlers(app);
        try
        {
            return app.Run(new MainWindow(windowId, WindowState.Load(windowId)));
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", ex.ToString());
            return 1;
        }
    }

    public static string NewId()
    {
        return Guid.NewGuid().ToString("N")[..12];
    }

    // Sortable creation timestamp up front so a directory listing of shells\ reads newest-last and the creation time is visible at a glance; the random suffix disambiguates shells created within the same second.
    public static string NewShellId()
    {
        return DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "-" + Guid.NewGuid().ToString("N")[..4];
    }

    private static bool TryClaimWindow(string windowId)
    {
        try
        {
            var state = WindowState.Load(windowId);
            var self = Process.GetCurrentProcess();
            if (state.OwnerPid != 0 && HostInfo.ProcessAlive(state.OwnerPid, state.OwnerStartTicks))
            {
                return false;
            }
            state.OwnerPid = self.Id;
            state.OwnerStartTicks = self.StartTime.Ticks;
            state.Save(windowId);
            Thread.Sleep(ClaimSettleMs);
            var check = WindowState.Load(windowId);
            return check.OwnerPid == self.Id && check.OwnerStartTicks == self.StartTime.Ticks;
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", $"window claim failed for {windowId}: {ex.Message}");
            return false;
        }
    }

    private static List<string> ListWindowIds()
    {
        var ids = new List<string>();
        try
        {
            foreach (var file in Directory.GetFiles(AppPaths.WindowsDir, "*.json"))
            {
                ids.Add(Path.GetFileNameWithoutExtension(file));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", "window scan failed: " + ex.Message);
        }
        return ids;
    }

    public static void SpawnWindowProcess(string windowId)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--window");
            psi.ArgumentList.Add(windowId);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", "window spawn failed: " + ex);
        }
    }
}
