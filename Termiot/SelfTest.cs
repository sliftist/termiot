using System.IO;
using System.Text;
using Termiot.Terminal;

namespace Termiot;

// --selftest: drives the full production pipeline headlessly — ShellSession spawns a real host, which runs a real cmd.exe under ConPTY; output flows back over the named pipe through the VtParser into a TermScreen. Passes when a command echo round-trips onto the screen. Results go to %LOCALAPPDATA%\Termiot\test-result.txt so the harness can read them even though this is a windowless exe.
public static class SelfTest
{
    private const int PromptTimeoutMs = 10000;
    private const int MarkerTimeoutMs = 10000;
    private const int PollMs = 100;
    private const string Marker = "selftest-marker-ABC123";

    public static int Run()
    {
        string resultPath = Path.Combine(AppPaths.Root, "test-result.txt");
        var sb = new StringBuilder();
        string shellId = "selftest-" + DateTime.Now.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4];
        int exitCode = 1;
        try
        {
            var screen = new TermScreen(120, 30);
            var parser = new VtParser(screen);
            var session = ShellSession.Create(shellId, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "selftest-window", screen, parser);
            int outputEvents = 0;
            session.OutputReceived += () => Interlocked.Increment(ref outputEvents);
            int exited = -999;
            session.Exited += code => exited = code;
            session.Begin();

            bool sawPrompt = WaitFor(() => ScreenText(screen).Contains('>'), PromptTimeoutMs);
            sb.AppendLine($"prompt: {sawPrompt} (outputEvents={outputEvents}, dead={session.Dead}, exited={exited})");

            session.SendText($"echo {Marker}\r");
            bool sawMarker = WaitFor(() => ScreenLines(screen).Any(l => l.Trim() == Marker), MarkerTimeoutMs);
            sb.AppendLine($"marker round-trip: {sawMarker} (outputEvents={outputEvents}, dead={session.Dead}, exited={exited})");

            sb.AppendLine("--- screen ---");
            foreach (var line in ScreenLines(screen).Where(l => l.Length > 0))
            {
                sb.AppendLine(line);
            }
            exitCode = sawMarker && !session.Dead ? 0 : 1;
            sb.AppendLine($"RESULT: {(exitCode == 0 ? "PASS" : "FAIL")}");
            session.ShutdownHost();
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        try
        {
            File.WriteAllText(resultPath, sb.ToString());
        }
        catch
        {
        }
        return exitCode;
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(PollMs);
        }
        return condition();
    }

    private static List<string> ScreenLines(TermScreen screen)
    {
        var lines = new List<string>();
        lock (screen.Sync)
        {
            for (int i = 0; i < screen.TotalLines; i++)
            {
                lines.Add(screen.GetLine(i).GetText());
            }
        }
        return lines;
    }

    private static string ScreenText(TermScreen screen)
    {
        return string.Join("\n", ScreenLines(screen));
    }
}
