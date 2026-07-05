using System.IO;

namespace Termiot;

public sealed class CommandHistory
{
    private const int MaxEntries = 5000;

    private readonly List<string> _entries = new();

    public CommandHistory()
    {
        try
        {
            if (File.Exists(AppPaths.HistoryFile))
            {
                _entries.AddRange(File.ReadAllLines(AppPaths.HistoryFile).Where(l => l.Length > 0));
                if (_entries.Count > MaxEntries)
                {
                    _entries.RemoveRange(0, _entries.Count - MaxEntries);
                    File.WriteAllLines(AppPaths.HistoryFile, _entries);
                }
            }
        }
        catch
        {
        }
    }

    public void Add(string command)
    {
        command = command.Trim();
        if (command.Length == 0 || (_entries.Count > 0 && _entries[^1] == command))
        {
            return;
        }
        _entries.Add(command);
        try
        {
            File.AppendAllLines(AppPaths.HistoryFile, new[] { command });
        }
        catch
        {
        }
    }

    // Most recent first, distinct; prefix "" matches everything so up-arrow on an empty box walks plain history.
    public List<string> Match(string prefix)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            if (entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(entry))
            {
                result.Add(entry);
            }
        }
        return result;
    }
}
