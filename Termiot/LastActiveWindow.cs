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
    private const uint MONITOR_DEFAULTTONEAREST = 2;

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
        var referenceMonitor = reference != IntPtr.Zero ? MonitorFromWindow(reference, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
        foreach (var record in AliveRecordsPreferring(referenceMonitor))
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

    private static List<Record> AliveRecordsPreferring(IntPtr monitor)
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
        return alive
            .OrderByDescending(r => monitor != IntPtr.Zero && MonitorFromWindow(new IntPtr(r.Hwnd), MONITOR_DEFAULTTONEAREST) == monitor)
            .ThenByDescending(r => r.LastActiveTicks)
            .ToList();
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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam, uint flags, uint timeoutMs, out IntPtr result);
}
