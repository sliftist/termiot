using System.Diagnostics;
using System.Text;

namespace Termiot;

// Millisecond timeline of renderer startup, flushed to app.log once the first frame is on screen. Cheap enough to leave on permanently — every launch records where the time went.
public static class StartupTrace
{
    private static readonly Stopwatch Watch = new();
    private static readonly List<(string Name, double Ms)> Marks = new();
    private static double _runtimeInitMs;

    public static void Init()
    {
        Watch.Start();
        try
        {
            using var self = Process.GetCurrentProcess();
            _runtimeInitMs = (DateTime.Now - self.StartTime).TotalMilliseconds;
        }
        catch
        {
        }
        Mark("main-entry");
    }

    public static void Mark(string name)
    {
        lock (Marks)
        {
            Marks.Add((name, _runtimeInitMs + Watch.Elapsed.TotalMilliseconds));
        }
    }

    public static void Flush()
    {
        var sb = new StringBuilder();
        sb.Append($"process-start→main {_runtimeInitMs:0}ms");
        lock (Marks)
        {
            double previous = _runtimeInitMs;
            foreach (var (name, ms) in Marks.Skip(1))
            {
                sb.Append($" | {name} +{ms - previous:0} (@{ms:0})");
                previous = ms;
            }
        }
        AppLog.Write("startup", sb.ToString());
    }
}
