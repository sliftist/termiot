using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows;

namespace Termiot;

public static class AppLog
{
    // Logging must never block the caller (it's invoked from the UI thread's hot paths). Lines are stamped on the caller's thread and appended to disk by a background worker, batching bursts into one write.
    private static readonly BlockingCollection<string> Queue = new();
    private static readonly object WriteLock = new();

    static AppLog()
    {
        new Thread(Loop) { IsBackground = true, Name = "app-log" }.Start();
    }

    public static void Write(string source, string message)
    {
        try
        {
            Queue.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] [{source}] {message}");
        }
        catch
        {
        }
    }

    private static void Loop()
    {
        foreach (var first in Queue.GetConsumingEnumerable())
        {
            var sb = new StringBuilder().Append(first).Append(Environment.NewLine);
            while (Queue.TryTake(out var more))
            {
                sb.Append(more).Append(Environment.NewLine);
            }
            Append(sb.ToString());
        }
    }

    // Drain and write any queued lines synchronously — call before the process exits (and after logging a crash) so nothing is lost.
    public static void Flush()
    {
        var sb = new StringBuilder();
        while (Queue.TryTake(out var line))
        {
            sb.Append(line).Append(Environment.NewLine);
        }
        if (sb.Length > 0)
        {
            Append(sb.ToString());
        }
    }

    private static void Append(string text)
    {
        try
        {
            lock (WriteLock)
            {
                File.AppendAllText(AppPaths.AppLogFile, text);
            }
        }
        catch
        {
        }
    }

    // WPF tears the process down on any unhandled exception; a terminal that loses all its tabs to one bad event handler is unacceptable, so log and keep running.
    public static void InstallCrashHandlers(Application? app)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Write("appdomain", e.ExceptionObject.ToString() ?? "unknown exception");
            Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("task", e.Exception.ToString());
            e.SetObserved();
        };
        if (app != null)
        {
            app.DispatcherUnhandledException += (_, e) =>
            {
                Write("dispatcher", e.Exception.ToString());
                e.Handled = true;
            };
        }
    }
}
