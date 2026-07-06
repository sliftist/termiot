using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Termiot;

// Per-window activity records (active-windows\<windowId>.json) let launchers route requests into running windows via WM_COPYDATA without any WPF cost. Open-tab requests prefer a window on the same monitor as the caller's foreground window (usually Cursor) — the user's terminals for a screen are usually ON that screen — falling back to the most recently used.
public static class LastActiveWindow
{
    public static readonly IntPtr OpenTabMessageId = (IntPtr)0x7E541072;
    public static readonly IntPtr EnsureShellMessageId = (IntPtr)0x7E541073;
    private const int WM_COPYDATA = 0x004A;
    private const int SendTimeoutMs = 3000;

    public sealed class Record
    {
        public int Pid { get; set; }
        public long StartTicks { get; set; }
        public long Hwnd { get; set; }
        public long LastActiveTicks { get; set; }
    }

    private static string Dir => Path.Combine(AppPaths.Root, "active-windows");

    private static string RecordPath(string windowId) => Path.Combine(Dir, windowId + ".json");

    public static void Save(string windowId, IntPtr hwnd)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            File.WriteAllText(RecordPath(windowId), JsonSerializer.Serialize(new Record
            {
                Pid = self.Id,
                StartTicks = self.StartTime.Ticks,
                Hwnd = hwnd.ToInt64(),
                LastActiveTicks = DateTime.UtcNow.Ticks,
            }));
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", "active-window save failed: " + ex.Message);
        }
    }

    public static void Remove(string windowId)
    {
        try
        {
            File.Delete(RecordPath(windowId));
        }
        catch
        {
        }
    }

    // Live record for a specific window, or null if it isn't running.
    public static Record? LoadAlive(string windowId)
    {
        try
        {
            var path = RecordPath(windowId);
            if (!File.Exists(path))
            {
                return null;
            }
            var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(path));
            if (record != null && HostInfo.ProcessAlive(record.Pid, record.StartTicks) && IsWindow(new IntPtr(record.Hwnd)))
            {
                return record;
            }
        }
        catch
        {
        }
        return null;
    }

    // True = an existing window took the tab; the caller should exit without any UI of its own.
    public static bool TryOpenTab(string cwd)
    {
        var reference = GetForegroundWindow();
        foreach (var record in AliveRecordsPreferring(reference))
        {
            if (SendCopyData(new IntPtr(record.Hwnd), OpenTabMessageId, cwd))
            {
                return true;
            }
        }
        return false;
    }

    public static bool SendEnsureShell(Record record, string shellId)
    {
        return SendCopyData(new IntPtr(record.Hwnd), EnsureShellMessageId, shellId);
    }

    // "Same screen" can't come from monitor handles — spanning setups (NVIDIA Surround) merge every panel into one logical monitor. Instead a window whose rect substantially overlaps the reference window's horizontal span (i.e. sits above/below it) is treated as on the same physical screen; without such a window, plain recency wins.
    private const double SameColumnMinOverlap = 0.5;

    private static List<Record> AliveRecordsPreferring(IntPtr reference)
    {
        var alive = new List<Record>();
        try
        {
            if (!Directory.Exists(Dir))
            {
                return alive;
            }
            foreach (var file in Directory.GetFiles(Dir, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                var record = LoadAlive(id);
                if (record != null)
                {
                    alive.Add(record);
                }
                else
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", "active-window scan failed: " + ex.Message);
        }
        bool haveReference = reference != IntPtr.Zero && GetWindowRect(reference, out var refRect) && refRect.Right > refRect.Left;
        return alive
            .OrderByDescending(r => haveReference && SharesColumn(reference, new IntPtr(r.Hwnd)))
            .ThenByDescending(r => r.LastActiveTicks)
            .ToList();
    }

    private static bool SharesColumn(IntPtr reference, IntPtr candidate)
    {
        if (!GetWindowRect(reference, out var a) || !GetWindowRect(candidate, out var b))
        {
            return false;
        }
        double overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        double narrower = Math.Min(a.Right - a.Left, b.Right - b.Left);
        return narrower > 0 && overlap / narrower >= SameColumnMinOverlap;
    }

    private static bool SendCopyData(IntPtr hwnd, IntPtr messageId, string payload)
    {
        try
        {
            var bytes = Encoding.Unicode.GetBytes(payload);
            var mem = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, mem, bytes.Length);
                var cds = new COPYDATASTRUCT { dwData = messageId, cbData = bytes.Length, lpData = mem };
                if (SendMessageTimeout(hwnd, WM_COPYDATA, IntPtr.Zero, ref cds, 0, SendTimeoutMs, out var result) == IntPtr.Zero)
                {
                    return false;
                }
                return result != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", "copydata send failed: " + ex.Message);
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam, uint flags, uint timeoutMs, out IntPtr result);
}
