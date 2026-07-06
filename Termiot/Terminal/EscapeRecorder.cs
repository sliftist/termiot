using System.IO;

namespace Termiot.Terminal;

// Collects every UNIQUE escape sequence we encounter but don't implement into %LOCALAPPDATA%\Termiot\unhandled-escapes.md — a live to-do list for parser coverage. Deduplication is by a normalized key (numeric parameters collapsed where they're positional, kept where they identify the feature, e.g. private mode numbers), so the file stays small no matter how much output flows. Each entry records one example sequence and the output text that preceded it, which is usually enough context to identify the emitting program and what the sequence is supposed to do.
public static class EscapeRecorder
{
    private static readonly object Lock = new();
    private static HashSet<string>? _seen;

    public static void Record(string key, string example, string contextBefore)
    {
        lock (Lock)
        {
            _seen ??= LoadSeen();
            if (!_seen.Add(key))
            {
                return;
            }
            try
            {
                using var stream = new FileStream(AppPaths.UnhandledEscapesFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.Write($"## {key}\n- first seen: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n- example: `{example}`\n- output before it: `{contextBefore}`\n\n");
            }
            catch
            {
            }
        }
    }

    private static HashSet<string> LoadSeen()
    {
        var seen = new HashSet<string>();
        try
        {
            if (File.Exists(AppPaths.UnhandledEscapesFile))
            {
                foreach (var line in File.ReadAllLines(AppPaths.UnhandledEscapesFile))
                {
                    if (line.StartsWith("## ", StringComparison.Ordinal))
                    {
                        seen.Add(line[3..]);
                    }
                }
            }
        }
        catch
        {
        }
        return seen;
    }
}
