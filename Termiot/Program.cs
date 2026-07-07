using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace Termiot;

public static class Program
{
    // Two processes racing for the same dead window both write their claim; the settle delay lets the last write win before each reads back to see who actually got it.
    private const int ClaimSettleMs = 150;

    // Set when Windows is shutting down / logging off; window closes during that must not count as deliberate.
    public static bool SessionEnding;

    [STAThread]
    public static int Main(string[] args)
    {
        StartupTrace.Init();
        if (args.Length >= 1 && args[0] == "--selftest")
        {
            AppLog.InstallCrashHandlers(null);
            return SelfTest.Run();
        }
        if (args.Length >= 1 && args[0] == "--profile-vt")
        {
            return ProfileVt(args);
        }
        if (args.Contains("-Embedding") || args.Contains("/Embedding"))
        {
            AppLog.InstallCrashHandlers(null);
            return TerminalHandoffServer.Run();
        }
        if (args.Length >= 7 && args[0] == "--handoffhost")
        {
            AppLog.InstallCrashHandlers(null);
            try
            {
                ShellHostProc.RunHandoff(args[1], long.Parse(args[2]), long.Parse(args[3]), long.Parse(args[4]), long.Parse(args[5]), long.Parse(args[6]));
            }
            catch (Exception ex)
            {
                AppLog.Write("shellhost", ex.ToString());
                return 1;
            }
            return 0;
        }
        if (args.Length >= 1 && args[0] == "--reopen")
        {
            // Startup-folder entry point: reopen every window that wasn't deliberately closed (shutdown, crash, power loss), then exit without opening anything of our own.
            AppLog.InstallCrashHandlers(null);
            foreach (var id in ListWindowIds())
            {
                var state = WindowState.Load(id);
                if (!state.ClosedCleanly && !(state.OwnerPid != 0 && HostInfo.ProcessAlive(state.OwnerPid, state.OwnerStartTicks)))
                {
                    SpawnWindowProcess(id);
                }
            }
            return 0;
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

        if (args.Length >= 1 && args[0] == "--ensure")
        {
            AppLog.InstallCrashHandlers(null);
            return EnsureTab(args) ? 0 : 1;
        }

        // Only the Cursor extension (HttpOpenHost) routes into an existing window as a tab — that's the "open fast" path. Every process launch (Explorer's --open, windowsExec, plain runs) deliberately opens its own new window.
        if (PrepareLaunch(args) is not { } plan)
        {
            return 0;
        }
        StartupTrace.Mark("window-claimed");
        Task.Run(() =>
        {
            StableLauncher.EnsureNewest();
            CursorExtension.EnsureUpToDate();
        });
        // Loading Consolas is the bulk of the glyph atlas cost; warming it on a worker overlaps that with WPF's own initialization.
        Task.Run(() =>
        {
            try
            {
                _ = new System.Windows.Media.FormattedText("M", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new System.Windows.Media.Typeface("Consolas"), 15, System.Windows.Media.Brushes.White, 1.0).WidthIncludingTrailingWhitespace;
            }
            catch
            {
            }
        });
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.SessionEnding += (_, _) => SessionEnding = true;
        AppLog.InstallCrashHandlers(app);
        var windowState = WindowState.Load(plan.WindowId);
        StartupTrace.Mark("app+state-ready");
        try
        {
            return app.Run(new MainWindow(plan.WindowId, windowState, plan.ForceResume, plan.TakeFocus));
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", ex.ToString());
            return 1;
        }
    }

    // Targeted launch: --ensure --window <name> --tab <name> [-d <dir>] [--cmd <command>]. Window missing → create it (with the tab). Window exists, tab missing → add the tab. Both exist → take the tab over: kill its shell and run the command. All existence checks and registrations go through the file system, written back immediately to shrink the create/create race window.
    private static bool EnsureTab(string[] args)
    {
        string windowName = "", tabName = "", dir = Environment.CurrentDirectory, command = "";
        int order = 0;
        for (int i = 1; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--window":
                    windowName = args[++i];
                    break;
                case "--tab":
                    tabName = args[++i];
                    break;
                case "-d":
                    dir = args[++i];
                    break;
                case "--cmd":
                    command = args[++i];
                    break;
                case "--order":
                    int.TryParse(args[++i], out order);
                    break;
            }
        }
        if (windowName.Length == 0 || tabName.Length == 0)
        {
            AppLog.Write("ui", "--ensure requires --window <name> and --tab <name>");
            return false;
        }
        AppLog.Write("ui", $"ensure: window='{windowName}' tab='{tabName}' dir='{dir}' cmd='{command}'");

        string? windowId = ListWindowIds().FirstOrDefault(id => string.Equals(WindowState.Load(id).Name, windowName, StringComparison.OrdinalIgnoreCase));
        if (windowId == null)
        {
            var shellId = SeedShell(tabName, dir, command, order);
            windowId = NewId();
            new WindowState { Name = windowName, Shells = new List<string> { shellId } }.Save(windowId);
            SpawnWindowProcess(windowId);
            return true;
        }

        var state = WindowState.Load(windowId);
        string? existingShellId = state.Shells.FirstOrDefault(id =>
            string.Equals((ShellInfo.Load(id) ?? new ShellInfo()).ForcedTitle, tabName, StringComparison.OrdinalIgnoreCase));
        bool windowAlive = LastActiveWindow.LoadAlive(windowId) is not null;
        if (existingShellId == null)
        {
            var shellId = SeedShell(tabName, dir, command, order);
            state = WindowState.Load(windowId);
            state.Shells.Insert(OrderedInsertIndex(state, order), shellId);
            state.Save(windowId);
            existingShellId = shellId;
        }
        else
        {
            // Takeover: replace the command and order, kill what's running; the resumed shell runs the new AUTORESUME.cmd.
            if (command.Length > 0)
            {
                File.WriteAllText(AppPaths.AutoResumeFile(existingShellId), command);
            }
            var info = ShellInfo.Load(existingShellId) ?? new ShellInfo();
            if (order != 0 && info.EnsureOrder != order)
            {
                ShellInfo.Save(new TabInfo { Id = existingShellId, Cwd = dir, Title = info.Title, ForcedTitle = info.ForcedTitle, PendingInput = info.PendingInput, EnsureOrder = order });
            }
            if (!windowAlive)
            {
                HostInfo.Kill(existingShellId);
            }
        }
        if (LastActiveWindow.LoadAlive(windowId) is { } record && LastActiveWindow.SendEnsureShell(record, existingShellId))
        {
            return true;
        }
        SpawnWindowProcess(windowId, resume: true);
        return true;
    }

    private static string SeedShell(string tabName, string dir, string command, int order)
    {
        var shellId = NewShellId();
        ShellInfo.Save(new TabInfo { Id = shellId, Cwd = dir, ForcedTitle = tabName, Title = tabName, EnsureOrder = order });
        if (command.Length > 0)
        {
            File.WriteAllText(AppPaths.AutoResumeFile(shellId), command);
        }
        return shellId;
    }

    private static int OrderedInsertIndex(WindowState state, int order)
    {
        for (int i = 0; i < state.Shells.Count; i++)
        {
            if ((ShellInfo.Load(state.Shells[i]) ?? new ShellInfo()).EnsureOrder > order)
            {
                return i;
            }
        }
        return state.Shells.Count;
    }

    // Measures pure VtParser throughput against real recorded logs (fed in 64 KB chunks, like replay). Results go to stdout and %TEMP%\termiot-vt-profile.txt.
    private static int ProfileVt(string[] args)
    {
        var sb = new StringBuilder();
        void Log(string s) => sb.AppendLine(s);

        List<string> files;
        if (args.Length >= 2 && File.Exists(args[1]))
        {
            files = new List<string> { args[1] };
        }
        else
        {
            files = Directory.GetFiles(AppPaths.ShellsDir, "output*.log", SearchOption.AllDirectories)
                .OrderByDescending(f => new FileInfo(f).Length)
                .Take(6)
                .ToList();
        }

        Log($"VT parser profile — {Environment.ProcessorCount} logical cores, .NET {Environment.Version}");
        Log("");
        Log($"{"log",-40}{"size",10}{"cold",10}{"best",10}{"MB/s",10}");

        long totalBytes = 0;
        double totalBestMs = 0;
        foreach (var file in files)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(file);
            }
            catch (Exception ex)
            {
                Log($"skip {file}: {ex.Message}");
                continue;
            }
            if (data.Length == 0)
            {
                continue;
            }

            double cold = RunOnce(data); // first pass: cold JIT + GC growth, like the app's one-and-only parse
            const int runs = 5;
            double best = double.MaxValue;
            for (int i = 0; i < runs; i++)
            {
                best = Math.Min(best, RunOnce(data));
            }
            double mb = data.Length / (1024.0 * 1024.0);
            string name = Path.GetFileName(Path.GetDirectoryName(file)) + "/" + Path.GetFileName(file);
            Log($"{name,-40}{mb,9:0.00}M{cold,9:0.0}ms{best,9:0.0}ms{mb / (best / 1000.0),10:0.0}");
            totalBytes += data.Length;
            totalBestMs += best;
        }

        double totalMb = totalBytes / (1024.0 * 1024.0);
        double mbPerSec = totalMb / (totalBestMs / 1000.0);
        Log("");
        Log($"total: {totalMb:0.00} MB in {totalBestMs:0.0} ms = {mbPerSec:0.0} MB/s ({1000.0 / mbPerSec:0.00} ms per MB)");
        Log($"→ a 100 KB tail replay ≈ {100.0 / 1024.0 / mbPerSec * 1000.0:0.00} ms of parsing");
        Log($"→ a full 10 MB log ≈ {10.0 / mbPerSec * 1000.0:0.0} ms of parsing");

        var text = sb.ToString();
        var outPath = Path.Combine(Path.GetTempPath(), "termiot-vt-profile.txt");
        try
        {
            File.WriteAllText(outPath, text);
        }
        catch
        {
        }
        Console.Write(text);
        return 0;
    }

    // One pass: fresh screen + parser, fed in replay-sized chunks. Returns milliseconds.
    private static double RunOnce(byte[] data)
    {
        var screen = new Terminal.TermScreen(120, 30) { ScrollbackCap = 1_000_000 };
        var parser = new Terminal.VtParser(screen);
        const int chunk = 64 * 1024;
        var sw = Stopwatch.StartNew();
        for (int off = 0; off < data.Length; off += chunk)
        {
            parser.Feed(data, off, Math.Min(chunk, data.Length - off));
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    public readonly record struct LaunchPlan(string WindowId, bool ForceResume, bool TakeFocus);

    // Everything a UI launch decides before a window exists: argument parsing, working-directory semantics, window claiming/seeding, and sibling restores. Shared verbatim by cold starts and warm-process activations. Null = nothing to do (duplicate claim).
    public static LaunchPlan? PrepareLaunch(string[] args)
    {
        AppLog.Write("ui", $"launch: args=[{string.Join(" | ", args)}], cwd={Environment.CurrentDirectory}");
        string? windowId = null;
        bool forceResume = false;
        bool takeFocus = false;
        bool explicitOpen = false;
        if (args.Length >= 2 && args[0] == "--open")
        {
            // Explorer context menu: a file resolves to its containing folder, a folder is used directly.
            var target = args[1];
            var dir = File.Exists(target) ? Path.GetDirectoryName(target) : target;
            if (dir != null && Directory.Exists(dir))
            {
                Environment.CurrentDirectory = dir;
                explicitOpen = true;
            }
            args = Array.Empty<string>();
        }
        if (args.Length >= 2 && args[0] == "--window")
        {
            windowId = args[1];
            forceResume = args.Contains("--resume");
            bool takeover = args.Contains("--takeover");
            if (!TryClaimWindow(windowId) && !(takeover && TakeOverWindow(windowId)))
            {
                AppLog.Write("ui", $"window {windowId} claim failed (owner alive or claim race lost) — exiting");
                return null;
            }
        }
        else
        {
            // Launched with a meaningful working directory (an external launcher like Cursor's Ctrl+Shift+C) → this process opens a fresh window THERE — and takes focus, since the user explicitly asked for a terminal. Dead windows restore as siblings. Otherwise this is a plain start and the first restorable window becomes ours.
            // Only windows that were NOT deliberately closed participate in restore; cleanly closed ones stay on disk for the settings Windows list.
            bool openHere = explicitOpen || HasMeaningfulCwd();
            takeFocus = openHere;
            var existing = ListWindowIds().Where(id => !WindowState.Load(id).ClosedCleanly).ToList();
            if (!openHere)
            {
                foreach (var id in existing)
                {
                    if (windowId == null && TryClaimWindow(id))
                    {
                        windowId = id;
                    }
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
                var shellId = NewShellId();
                ShellInfo.Save(new TabInfo { Id = shellId, Cwd = Environment.CurrentDirectory });
                new WindowState { Shells = new List<string> { shellId } }.Save(windowId);
                TryClaimWindow(windowId);
            }
        }
        return new LaunchPlan(windowId, forceResume, takeFocus);
    }

    // Explorer launches set cwd to the exe folder, Start menu to system32 — anything else means the launcher chose a directory on purpose.
    private static bool HasMeaningfulCwd()
    {
        var cwd = Environment.CurrentDirectory.TrimEnd('\\');
        var exeDir = (Path.GetDirectoryName(Environment.ProcessPath) ?? "").TrimEnd('\\');
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System).TrimEnd('\\');
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        return !string.Equals(cwd, exeDir, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(cwd, system, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(cwd, windows, StringComparison.OrdinalIgnoreCase);
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

    // Rebuild-and-reload: the freshly built process kills the window's current (verified alive, pid + start time matched) owner and takes its place. Only used after a successful build, so a failed build never costs the running window.
    private static bool TakeOverWindow(string windowId)
    {
        const int OwnerExitTimeoutMs = 5000;
        var state = WindowState.Load(windowId);
        if (state.OwnerPid != 0 && HostInfo.ProcessAlive(state.OwnerPid, state.OwnerStartTicks))
        {
            try
            {
                Process.GetProcessById(state.OwnerPid).Kill();
            }
            catch (Exception ex)
            {
                AppLog.Write("ui", $"takeover kill failed for {windowId}: {ex.Message}");
            }
            var deadline = Environment.TickCount64 + OwnerExitTimeoutMs;
            while (HostInfo.ProcessAlive(state.OwnerPid, state.OwnerStartTicks) && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(100);
            }
        }
        return TryClaimWindow(windowId);
    }

    private static bool TryClaimWindow(string windowId)
    {
        try
        {
            // Fresh window ids are random — nobody can be racing for a file that doesn't exist yet, so the settle wait is only paid when claiming an existing window (restore/takeover races).
            bool contested = File.Exists(AppPaths.WindowFile(windowId));
            var state = WindowState.Load(windowId);
            var self = Process.GetCurrentProcess();
            if (state.OwnerPid != 0 && HostInfo.ProcessAlive(state.OwnerPid, state.OwnerStartTicks))
            {
                return false;
            }
            state.OwnerPid = self.Id;
            state.OwnerStartTicks = self.StartTime.Ticks;
            state.Save(windowId);
            if (!contested)
            {
                return true;
            }
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

    public static void SpawnWindowProcess(string windowId, bool resume = false)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--window");
            psi.ArgumentList.Add(windowId);
            if (resume)
            {
                psi.ArgumentList.Add("--resume");
            }
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", "window spawn failed: " + ex);
        }
    }
}
