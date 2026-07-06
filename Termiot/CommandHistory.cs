using System.IO;

namespace Termiot;

// Per-shell command history: each tab remembers only what was run in it (file lives in the shell's own folder), so up-arrow and history-based autocomplete never surface other tabs' commands.
public sealed class CommandHistory
{
    private const int MaxEntries = 5000;

    private readonly string _file;
    private readonly List<string> _entries = new();

    public CommandHistory(string file)
    {
        _file = file;
        try
        {
            if (File.Exists(_file))
            {
                _entries.AddRange(File.ReadAllLines(_file).Where(l => l.Length > 0));
                if (_entries.Count > MaxEntries)
                {
                    _entries.RemoveRange(0, _entries.Count - MaxEntries);
                    File.WriteAllLines(_file, _entries);
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
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.AppendAllLines(_file, new[] { command });
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
