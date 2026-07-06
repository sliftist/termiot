using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Termiot;

public sealed class ResourceUsage
{
    public double CpuPercent;
    public long MemoryBytes;
    public long GpuBytes;
}

// Background sampler for per-tab resource usage. A tab's usage is its whole process tree (shell host → cmd → whatever it spawned), discovered via a Toolhelp snapshot from the host pid in host.json. Memory is the summed working set, CPU is the tree's processor-time delta averaged over the last minute, and dedicated GPU memory comes from the "GPU Process Memory" performance counters read through PDH (no package dependency).
public sealed class ResourceSampler
{
    private const int SampleIntervalMs = 1500;
    private const int CpuWindowSeconds = 60;

    private volatile string[] _shellIds = Array.Empty<string>();
    private readonly object _sync = new();
    private readonly Dictionary<string, ResourceUsage> _usage = new();
    private readonly Dictionary<int, TimeSpan> _lastCpuByPid = new();
    private readonly Dictionary<string, Queue<(long Ticks, double Pct)>> _cpuWindows = new();
    private long _lastSampleTicks;

    public ResourceSampler()
    {
        new Thread(SampleLoop) { IsBackground = true, Name = "resource-sampler" }.Start();
    }

    public void SetShells(IEnumerable<string> shellIds)
    {
        _shellIds = shellIds.ToArray();
    }

    public ResourceUsage? Get(string shellId)
    {
        lock (_sync)
        {
            return _usage.TryGetValue(shellId, out var usage) ? usage : null;
        }
    }

    private void SampleLoop()
    {
        _lastSampleTicks = Stopwatch.GetTimestamp();
        while (true)
        {
            Thread.Sleep(SampleIntervalMs);
            try
            {
                SampleOnce();
            }
            catch (Exception ex)
            {
                AppLog.Write("resources", "sample failed: " + ex.Message);
            }
        }
    }

    private void SampleOnce()
    {
        var shells = _shellIds;
        if (shells.Length == 0)
        {
            return;
        }
        long now = Stopwatch.GetTimestamp();
        double wallSeconds = (now - _lastSampleTicks) / (double)Stopwatch.Frequency;
        _lastSampleTicks = now;

        var children = SnapshotProcessTree();
        var gpuByPid = ReadGpuDedicatedByPid();
        var seenPids = new HashSet<int>();
        var results = new Dictionary<string, ResourceUsage>();
        foreach (var shellId in shells)
        {
            int hostPid = ReadHostPid(shellId);
            if (hostPid == 0)
            {
                continue;
            }
            var usage = new ResourceUsage();
            double cpuDeltaSeconds = 0;
            foreach (var pid in Descendants(hostPid, children))
            {
                seenPids.Add(pid);
                try
                {
                    using var process = Process.GetProcessById(pid);
                    usage.MemoryBytes += process.WorkingSet64;
                    var total = process.TotalProcessorTime;
                    if (_lastCpuByPid.TryGetValue(pid, out var previous))
                    {
                        cpuDeltaSeconds += Math.Max(0, (total - previous).TotalSeconds);
                    }
                    _lastCpuByPid[pid] = total;
                }
                catch
                {
                }
                if (gpuByPid.TryGetValue(pid, out var gpu))
                {
                    usage.GpuBytes += gpu;
                }
            }
            double instantPct = wallSeconds > 0 ? cpuDeltaSeconds / wallSeconds / Environment.ProcessorCount * 100 : 0;
            usage.CpuPercent = RollCpuWindow(shellId, now, instantPct);
            results[shellId] = usage;
        }
        foreach (var stale in _lastCpuByPid.Keys.Where(pid => !seenPids.Contains(pid)).ToList())
        {
            _lastCpuByPid.Remove(stale);
        }
        lock (_sync)
        {
            _usage.Clear();
            foreach (var pair in results)
            {
                _usage[pair.Key] = pair.Value;
            }
        }
    }

    private double RollCpuWindow(string shellId, long nowTicks, double instantPct)
    {
        if (!_cpuWindows.TryGetValue(shellId, out var window))
        {
            window = new Queue<(long, double)>();
            _cpuWindows[shellId] = window;
        }
        window.Enqueue((nowTicks, instantPct));
        long cutoff = nowTicks - CpuWindowSeconds * Stopwatch.Frequency;
        while (window.Count > 0 && window.Peek().Ticks < cutoff)
        {
            window.Dequeue();
        }
        return window.Average(s => s.Pct);
    }

    private static int ReadHostPid(string shellId)
    {
        try
        {
            var path = AppPaths.HostInfoFile(shellId);
            if (!File.Exists(path))
            {
                return 0;
            }
            var host = JsonSerializer.Deserialize<HostInfo>(File.ReadAllText(path));
            return host != null && HostInfo.ProcessAlive(host.Pid, host.StartTicks) ? host.Pid : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static IEnumerable<int> Descendants(int rootPid, Dictionary<int, List<int>> children)
    {
        var pending = new Stack<int>();
        pending.Push(rootPid);
        while (pending.Count > 0)
        {
            int pid = pending.Pop();
            yield return pid;
            if (children.TryGetValue(pid, out var kids))
            {
                foreach (var kid in kids)
                {
                    pending.Push(kid);
                }
            }
        }
    }

    // --- Toolhelp process snapshot (pid → children) ---

    private static Dictionary<int, List<int>> SnapshotProcessTree()
    {
        var children = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return children;
        }
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return children;
            }
            do
            {
                if (!children.TryGetValue((int)entry.th32ParentProcessID, out var list))
                {
                    list = new List<int>();
                    children[(int)entry.th32ParentProcessID] = list;
                }
                list.Add((int)entry.th32ProcessID);
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return children;
    }

    // --- PDH: dedicated GPU memory per process ---

    private static Dictionary<int, long> ReadGpuDedicatedByPid()
    {
        var result = new Dictionary<int, long>();
        if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0)
        {
            return result;
        }
        try
        {
            if (PdhAddEnglishCounterW(query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out var counter) != 0)
            {
                return result;
            }
            if (PdhCollectQueryData(query) != 0)
            {
                return result;
            }
            uint bufferSize = 0, itemCount = 0;
            PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, ref bufferSize, ref itemCount, IntPtr.Zero);
            if (bufferSize == 0)
            {
                return result;
            }
            var buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, ref bufferSize, ref itemCount, buffer) != 0)
                {
                    return result;
                }
                int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
                for (int i = 0; i < itemCount; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(buffer + i * itemSize);
                    var name = Marshal.PtrToStringUni(item.szName) ?? "";
                    // Instance names look like "pid_1234_luid_0x..._phys_0".
                    if (name.StartsWith("pid_") && item.FmtValue.CStatus == 0)
                    {
                        int end = name.IndexOf('_', 4);
                        if (end > 4 && int.TryParse(name[4..end], out int pid))
                        {
                            result[pid] = result.GetValueOrDefault(pid) + item.FmtValue.largeValue;
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            PdhCloseQuery(query);
        }
        return result;
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const uint PDH_FMT_LARGE = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        private uint _padding;
        public long largeValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}
