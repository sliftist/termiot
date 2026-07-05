using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Termiot;

public static class AppLog
{
    private static readonly object Lock = new();

    public static void Write(string source, string message)
    {
        try
        {
            lock (Lock)
            {
                File.AppendAllText(AppPaths.AppLogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] [{source}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    // WPF tears the process down on any unhandled exception; a terminal that loses all its tabs to one bad event handler is unacceptable, so log and keep running.
    public static void InstallCrashHandlers(Application? app)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("appdomain", e.ExceptionObject.ToString() ?? "unknown exception");
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
