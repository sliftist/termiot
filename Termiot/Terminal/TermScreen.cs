namespace Termiot.Terminal;

// The screen model: a grid of TermLine objects plus an unbounded-ish scrollback. Lines scroll off the top by moving the line object itself into the scrollback list, so annotations and content survive without copying. All mutation and read access must happen under Sync.
public sealed class TermScreen
{
    public const int DefaultScrollbackCap = 1_000_000;
    private const int ScrollbackTrimChunk = 10_000;

    // Settable at runtime (settings → scrollback history); shrinking takes effect lazily on the next scrolled-off line.
    public int ScrollbackCap { get; set; } = DefaultScrollbackCap;
    private const int TabWidth = 8;

    public readonly object Sync = new();

    private int _cols;
    private int _rows;
    private readonly List<TermLine> _scrollback = new();
    private TermLine[] _lines;
    private TermLine[]? _mainSaved;
    private int _x;
    private int _y;
    private int _savedX;
    private int _savedY;
    private uint _savedFg = Palette.DefaultFg;
    private uint _savedBg = Palette.DefaultBg;
    private CellFlags _savedFlags;
    private bool _pendingWrap;
    private int _marginTop;
    private int _marginBottom;

    public uint CurFg = Palette.DefaultFg;
    public uint CurBg = Palette.DefaultBg;
    public CellFlags CurFlags;
    public bool CursorVisible = true;

    public int Cols => _cols;
    public int Rows => _rows;
    public int CursorX => _x;
    public int CursorY => _y;
    public bool OnAltScreen => _mainSaved != null;
    public int ScrollbackCount => _scrollback.Count;
    public int TotalLines => _scrollback.Count + _rows;
    // Total lines ever dropped off the front of scrollback (cap trimming). Absolute line indices shift down when this grows, so incremental consumers (search) rescan when it changes.
    public long DroppedLines { get; private set; }

    // Approximate character count of the whole buffer: sum of each line's cell array length (scrollback lines are trimmed of trailing blanks, so this tracks real content). O(lines), cheap; caller must hold Sync.
    public long TotalChars()
    {
        long total = 0;
        foreach (var line in _scrollback)
        {
            total += line.Cells.Length;
        }
        foreach (var line in _lines)
        {
            total += line.Cells.Length;
        }
        return total;
    }

    public TermScreen(int cols, int rows)
    {
        _cols = Math.Max(2, cols);
        _rows = Math.Max(2, rows);
        _lines = NewLines(_rows);
        _marginBottom = _rows - 1;
    }

    private Cell Blank => new() { Ch = ' ', Fg = Palette.DefaultFg, Bg = CurBg, Flags = CellFlags.None };

    private Cell DefaultBlank => new() { Ch = ' ', Fg = Palette.DefaultFg, Bg = Palette.DefaultBg, Flags = CellFlags.None };

    private TermLine[] NewLines(int rows)
    {
        var lines = new TermLine[rows];
        for (int i = 0; i < rows; i++)
        {
            lines[i] = new TermLine(_cols, DefaultBlank);
        }
        return lines;
    }

    public TermLine GetLine(int absIndex)
    {
        return absIndex < _scrollback.Count ? _scrollback[absIndex] : _lines[absIndex - _scrollback.Count];
    }

    public void Print(char ch)
    {
        if (_pendingWrap)
        {
            _pendingWrap = false;
            _x = 0;
            LineFeed();
        }
        _lines[_y].Cells[_x] = new Cell { Ch = ch, Fg = CurFg, Bg = CurBg, Flags = CurFlags };
        if (_x == _cols - 1)
        {
            _pendingWrap = true;
        }
        else
        {
            _x++;
        }
    }

    public void PrintEscapeText(string text, string desc)
    {
        TermLine? runLine = null;
        int runStart = 0;
        int runLength = 0;
        foreach (var ch in text)
        {
            if (_pendingWrap)
            {
                FlushRun();
                _pendingWrap = false;
                _x = 0;
                LineFeed();
            }
            if (!ReferenceEquals(runLine, _lines[_y]))
            {
                FlushRun();
                runLine = _lines[_y];
                runStart = _x;
                runLength = 0;
            }
            _lines[_y].Cells[_x] = new Cell { Ch = ch, Fg = CurFg, Bg = CurBg, Flags = CurFlags | CellFlags.Escape };
            runLength++;
            if (_x == _cols - 1)
            {
                _pendingWrap = true;
            }
            else
            {
                _x++;
            }
        }
        FlushRun();

        void FlushRun()
        {
            if (runLine == null || runLength == 0)
            {
                return;
            }
            (runLine.Annotations ??= new List<LineAnnotation>()).Add(new LineAnnotation { Start = runStart, Length = runLength, Desc = desc });
            runLine = null;
        }
    }

    public void LineFeed()
    {
        _pendingWrap = false;
        if (_y == _marginBottom)
        {
            ScrollUp(1);
        }
        else if (_y < _rows - 1)
        {
            _y++;
        }
    }

    public void ReverseLineFeed()
    {
        _pendingWrap = false;
        if (_y == _marginTop)
        {
            ScrollDown(1);
        }
        else if (_y > 0)
        {
            _y--;
        }
    }

    public void CarriageReturn()
    {
        _x = 0;
        _pendingWrap = false;
    }

    public void Backspace()
    {
        if (_x > 0)
        {
            _x--;
        }
        _pendingWrap = false;
    }

    public void HorizontalTab()
    {
        _x = Math.Min((_x / TabWidth + 1) * TabWidth, _cols - 1);
        _pendingWrap = false;
    }

    public void ScrollUp(int n)
    {
        n = Math.Clamp(n, 1, _marginBottom - _marginTop + 1);
        for (int i = 0; i < n; i++)
        {
            if (_marginTop == 0 && !OnAltScreen)
            {
                PushScrollback(_lines[0]);
            }
            for (int y = _marginTop; y < _marginBottom; y++)
            {
                _lines[y] = _lines[y + 1];
            }
            _lines[_marginBottom] = new TermLine(_cols, Blank);
        }
    }

    public void ScrollDown(int n)
    {
        n = Math.Clamp(n, 1, _marginBottom - _marginTop + 1);
        for (int i = 0; i < n; i++)
        {
            for (int y = _marginBottom; y > _marginTop; y--)
            {
                _lines[y] = _lines[y - 1];
            }
            _lines[_marginTop] = new TermLine(_cols, Blank);
        }
    }

    private void PushScrollback(TermLine line)
    {
        line.TrimTrailingBlanks();
        _scrollback.Add(line);
        if (_scrollback.Count > ScrollbackCap + ScrollbackTrimChunk)
        {
            int removed = _scrollback.Count - ScrollbackCap;
            _scrollback.RemoveRange(0, removed);
            DroppedLines += removed;
        }
    }

    // Insert already-rendered lines at the very top of scrollback (older than everything currently held). Staged restore shows the recent tail first, then parses the older history on a background thread and prepends the result here. Lines go in as-is: scrollback rows are variable-width by design (trailing blanks trimmed), so widening them here would be both wrong and — since it reallocates every row under Sync — a paint-blocking stall.
    public void PrependScrollback(IReadOnlyList<TermLine> older)
    {
        if (older.Count == 0)
        {
            return;
        }
        _scrollback.InsertRange(0, older);
        if (_scrollback.Count > ScrollbackCap + ScrollbackTrimChunk)
        {
            _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackCap);
        }
    }

    // A frozen top-to-bottom snapshot of every line this screen holds (scrollback, then the live rows down to the last non-empty one) — used to lift a scratch screen's parsed history into another screen's scrollback.
    public List<TermLine> SnapshotLines()
    {
        var result = new List<TermLine>(_scrollback);
        int lastNonBlank = -1;
        for (int i = 0; i < _rows; i++)
        {
            if (_lines[i].GetText().Length > 0)
            {
                lastNonBlank = i;
            }
        }
        for (int i = 0; i <= lastNonBlank; i++)
        {
            // Match scrollback convention (trimmed, variable-width) so prepend never has to touch these rows.
            _lines[i].TrimTrailingBlanks();
            result.Add(_lines[i]);
        }
        return result;
    }

    public void SetCursorPos(int row, int col)
    {
        _y = Math.Clamp(row, 0, _rows - 1);
        _x = Math.Clamp(col, 0, _cols - 1);
        _pendingWrap = false;
    }

    public void MoveCursor(int dRow, int dCol)
    {
        SetCursorPos(_y + dRow, _x + dCol);
    }

    public void SetCursorCol(int col)
    {
        SetCursorPos(_y, col);
    }

    public void SetCursorRow(int row)
    {
        SetCursorPos(row, _x);
    }

    public void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (int y = _y + 1; y < _rows; y++)
                {
                    ClearLine(_lines[y]);
                }
                break;
            case 1:
                for (int y = 0; y < _y; y++)
                {
                    ClearLine(_lines[y]);
                }
                EraseInLine(1);
                break;
            case 2:
                if (!OnAltScreen)
                {
                    foreach (var line in _lines)
                    {
                        if (line.GetText().Length > 0)
                        {
                            PushScrollback(line);
                        }
                    }
                    _lines = NewLines(_rows);
                }
                else
                {
                    foreach (var line in _lines)
                    {
                        ClearLine(line);
                    }
                }
                break;
            case 3:
                _scrollback.Clear();
                break;
        }
    }

    public void EraseInLine(int mode)
    {
        var cells = _lines[_y].Cells;
        int from = mode == 0 ? _x : 0;
        int to = mode == 1 ? _x : _cols - 1;
        for (int x = from; x <= to; x++)
        {
            cells[x] = Blank;
        }
        _pendingWrap = false;
    }

    private void ClearLine(TermLine line)
    {
        Array.Fill(line.Cells, Blank);
        line.Annotations = null;
    }

    public void InsertLines(int n)
    {
        if (_y < _marginTop || _y > _marginBottom)
        {
            return;
        }
        n = Math.Clamp(n, 1, _marginBottom - _y + 1);
        for (int i = 0; i < n; i++)
        {
            for (int y = _marginBottom; y > _y; y--)
            {
                _lines[y] = _lines[y - 1];
            }
            _lines[_y] = new TermLine(_cols, Blank);
        }
    }

    public void DeleteLines(int n)
    {
        if (_y < _marginTop || _y > _marginBottom)
        {
            return;
        }
        n = Math.Clamp(n, 1, _marginBottom - _y + 1);
        for (int i = 0; i < n; i++)
        {
            for (int y = _y; y < _marginBottom; y++)
            {
                _lines[y] = _lines[y + 1];
            }
            _lines[_marginBottom] = new TermLine(_cols, Blank);
        }
    }

    public void InsertChars(int n)
    {
        var cells = _lines[_y].Cells;
        n = Math.Clamp(n, 1, _cols - _x);
        for (int x = _cols - 1; x >= _x + n; x--)
        {
            cells[x] = cells[x - n];
        }
        for (int x = _x; x < _x + n; x++)
        {
            cells[x] = Blank;
        }
    }

    public void DeleteChars(int n)
    {
        var cells = _lines[_y].Cells;
        n = Math.Clamp(n, 1, _cols - _x);
        for (int x = _x; x < _cols - n; x++)
        {
            cells[x] = cells[x + n];
        }
        for (int x = _cols - n; x < _cols; x++)
        {
            cells[x] = Blank;
        }
    }

    public void EraseChars(int n)
    {
        var cells = _lines[_y].Cells;
        n = Math.Clamp(n, 1, _cols - _x);
        for (int x = _x; x < _x + n; x++)
        {
            cells[x] = Blank;
        }
    }

    public void SetMargins(int top, int bottom)
    {
        top = Math.Clamp(top, 0, _rows - 2);
        bottom = Math.Clamp(bottom, top + 1, _rows - 1);
        _marginTop = top;
        _marginBottom = bottom;
        SetCursorPos(0, 0);
    }

    public void UseAltScreen(bool on, bool clear)
    {
        if (on && !OnAltScreen)
        {
            _mainSaved = _lines;
            _lines = NewLines(_rows);
            SetCursorPos(0, 0);
        }
        else if (!on && OnAltScreen)
        {
            _lines = _mainSaved!;
            _mainSaved = null;
        }
        _marginTop = 0;
        _marginBottom = _rows - 1;
        if (clear && OnAltScreen)
        {
            foreach (var line in _lines)
            {
                ClearLine(line);
            }
        }
    }

    public void SaveCursor()
    {
        _savedX = _x;
        _savedY = _y;
        _savedFg = CurFg;
        _savedBg = CurBg;
        _savedFlags = CurFlags;
    }

    public void RestoreCursor()
    {
        SetCursorPos(_savedY, _savedX);
        CurFg = _savedFg;
        CurBg = _savedBg;
        CurFlags = _savedFlags;
    }

    public void FullReset()
    {
        UseAltScreen(false, false);
        foreach (var line in _lines)
        {
            ClearLine(line);
        }
        CurFg = Palette.DefaultFg;
        CurBg = Palette.DefaultBg;
        CurFlags = CellFlags.None;
        CursorVisible = true;
        _marginTop = 0;
        _marginBottom = _rows - 1;
        SetCursorPos(0, 0);
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols);
        rows = Math.Max(2, rows);
        if (cols == _cols && rows == _rows)
        {
            return;
        }
        _cols = cols;
        foreach (var line in _lines)
        {
            line.Resize(cols, DefaultBlank);
        }
        if (_mainSaved != null)
        {
            foreach (var line in _mainSaved)
            {
                line.Resize(cols, DefaultBlank);
            }
        }
        if (rows != _rows)
        {
            _lines = ResizeRows(_lines, rows, !OnAltScreen);
            if (_mainSaved != null)
            {
                _mainSaved = ResizeRows(_mainSaved, rows, false);
            }
            _rows = rows;
        }
        _marginTop = 0;
        _marginBottom = _rows - 1;
        _y = Math.Clamp(_y, 0, _rows - 1);
        _x = Math.Clamp(_x, 0, _cols - 1);
        _pendingWrap = false;
    }

    // Shrinking pushes top lines into scrollback and growing pulls them back, so the prompt hugs the bottom of the window across resizes instead of drifting up.
    private TermLine[] ResizeRows(TermLine[] lines, int rows, bool useScrollback)
    {
        var result = new List<TermLine>(lines);
        while (result.Count > rows)
        {
            if (useScrollback)
            {
                PushScrollback(result[0]);
            }
            result.RemoveAt(0);
            if (_y > 0)
            {
                _y--;
            }
        }
        while (result.Count < rows)
        {
            if (useScrollback && _scrollback.Count > 0)
            {
                var pulled = _scrollback[^1];
                _scrollback.RemoveAt(_scrollback.Count - 1);
                pulled.Resize(_cols, DefaultBlank);
                result.Insert(0, pulled);
                _y++;
            }
            else
            {
                result.Add(new TermLine(_cols, DefaultBlank));
            }
        }
        return result.ToArray();
    }
}
