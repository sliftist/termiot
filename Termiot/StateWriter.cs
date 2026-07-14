using System.IO;
using System.Text;

namespace Termiot;

// Off-thread persistence for small state files (shell.json, window json, settings, last-active records). Callers serialize on their own thread (cheap, CPU-only) and hand the finished bytes here; the actual disk write happens on a dedicated background thread so it never blocks the UI. Writes coalesce per path (only the latest content for a path is written) and a single drainer serializes them, so a burst of saves to the same file collapses to one write and can't interleave. Flush() forces pending writes out synchronously — call it before the process exits so nothing is lost.
public static class StateWriter
{
    private static readonly object Sync = new();
    private static readonly object DrainLock = new();
    // path -> latest content, or null to mean "delete this path".
    private static readonly Dictionary<string, byte[]?> Pending = new();
    private static readonly AutoResetEvent Signal = new(false);

    static StateWriter()
    {
        new Thread(Loop) { IsBackground = true, Name = "state-writer" }.Start();
    }

    public static void Write(string path, string content)
    {
        Enqueue(path, Encoding.UTF8.GetBytes(content));
    }

    public static void Delete(string path)
    {
        Enqueue(path, null);
    }

    private static void Enqueue(string path, byte[]? content)
    {
        lock (Sync)
        {
            Pending[path] = content;
        }
        Signal.Set();
    }

    private static void Loop()
    {
        while (true)
        {
            Signal.WaitOne();
            Drain();
        }
    }

    // Synchronously write everything currently pending. Safe to call from any thread; the drain lock keeps it from racing the background thread.
    public static void Flush()
    {
        Drain();
    }

    private static void Drain()
    {
        lock (DrainLock)
        {
            while (true)
            {
                List<KeyValuePair<string, byte[]?>> batch;
                lock (Sync)
                {
                    if (Pending.Count == 0)
                    {
                        return;
                    }
                    batch = new List<KeyValuePair<string, byte[]?>>(Pending);
                    Pending.Clear();
                }
                foreach (var (path, content) in batch)
                {
                    try
                    {
                        if (content == null)
                        {
                            File.Delete(path);
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            File.WriteAllBytes(path, content);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("state", $"async write failed for {path}: {ex.Message}");
                    }
                }
            }
        }
    }
}
