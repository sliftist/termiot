using System.IO;

namespace Termiot;

// Per-shell command history: each tab remembers only what was run in it (file lives in the shell's own folder), so up-arrow and history-based autocomplete never surface other tabs' commands. All disk access is off the UI thread — the file read can stall on a slow/network drive, and blocking window construction or the Enter keypress on it is never acceptable.
public sealed class CommandHistory
{
    private const int MaxEntries = 5000;

    private readonly string _file;
    private readonly List<string> _entries = new();
    private readonly object _gate = new();

    public CommandHistory(string file)
    {
        _file = file;
        Task.Run(Load);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file))
            {
                return;
            }
            var fileEntries = File.ReadAllLines(_file).Where(l => l.Length > 0).ToList();
            bool rewrite = false;
            lock (_gate)
            {
                // Anything the user ran before the load finished stays newest; file history goes in front of it.
                _entries.InsertRange(0, fileEntries);
                if (_entries.Count > MaxEntries)
                {
                    _entries.RemoveRange(0, _entries.Count - MaxEntries);
                    rewrite = true;
                }
            }
            if (rewrite)
            {
                string[] snapshot;
                lock (_gate)
                {
                    snapshot = _entries.ToArray();
                }
                File.WriteAllLines(_file, snapshot);
            }
        }
        catch
        {
        }
    }

    public void Add(string command)
    {
        command = command.Trim();
        lock (_gate)
        {
            if (command.Length == 0 || (_entries.Count > 0 && _entries[^1] == command))
            {
                return;
            }
            _entries.Add(command);
        }
        // Append off the UI thread; the _gate serializes writers so lines can't interleave.
        Task.Run(() =>
        {
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                    File.AppendAllLines(_file, new[] { command });
                }
                catch
                {
                }
            }
        });
    }

    // Forget every remembered command for this shell and remove the backing file (recreated on the next Add).
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
        Task.Run(() =>
        {
            lock (_gate)
            {
                try
                {
                    File.Delete(_file);
                }
                catch
                {
                }
            }
        });
    }

    // Most recent first, distinct; prefix "" matches everything so up-arrow on an empty box walks plain history.
    public List<string> Match(string prefix)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(entry))
                {
                    result.Add(entry);
                }
            }
        }
        return result;
    }
}
