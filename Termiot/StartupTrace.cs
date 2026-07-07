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
        AppLog.Write("startup", Format(" | "));
    }

    // The timeline as text; each entry is "name +delta (@absolute)" in ms since process start. Used by both the log flush (single line) and the settings Startup tab (one per line).
    public static string Format(string separator)
    {
        var sb = new StringBuilder();
        sb.Append($"process-start→main {_runtimeInitMs:0}ms");
        lock (Marks)
        {
            // Sort by timestamp: marks arrive from both the UI thread and the background restore thread, so insertion order isn't time order and unsorted deltas would go negative.
            var ordered = Marks.OrderBy(m => m.Ms).ToList();
            double previous = _runtimeInitMs;
            foreach (var (name, ms) in ordered)
            {
                if (name == "main-entry")
                {
                    continue;
                }
                sb.Append($"{separator}{name} +{ms - previous:0} (@{ms:0})");
                previous = ms;
            }
        }
        return sb.ToString();
    }
}
