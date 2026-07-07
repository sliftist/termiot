using System.Text;

namespace Termiot;

// In-memory ring of recent performance lines (per-tab history transfer/parse timings), surfaced in the settings Profiling tab so they don't have to be dug out of app.log. Also mirrored to app.log.
public static class PerfLog
{
    private const int MaxEntries = 200;
    private static readonly List<string> Entries = new();

    public static void Record(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        lock (Entries)
        {
            Entries.Add(stamped);
            if (Entries.Count > MaxEntries)
            {
                Entries.RemoveRange(0, Entries.Count - MaxEntries);
            }
        }
        AppLog.Write("perf", line);
    }

    // Most recent first.
    public static string Format(string separator)
    {
        lock (Entries)
        {
            if (Entries.Count == 0)
            {
                return "No profiling recorded yet — reload a window with a heavy shell and the per-tab transfer/parse timings will appear here.";
            }
            var sb = new StringBuilder();
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (sb.Length > 0)
                {
                    sb.Append(separator);
                }
                sb.Append(Entries[i]);
            }
            return sb.ToString();
        }
    }
}
