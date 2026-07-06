using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Termiot.Terminal;

namespace Termiot.Ui;

public sealed class SearchSpan
{
    public int Col;
    public int Len;
    public bool Current;
}

// Renders the attached TermScreen into a WriteableBitmap at device-pixel resolution: background fill per cell, then alpha-blended glyphs from the GlyphAtlas. Scroll position is tracked as lines up from the bottom so live output naturally stays pinned.
public sealed class TerminalControl : FrameworkElement
{
    private const int ScrollLinesPerNotch = 3;
    private const int WheelDeltaPerNotch = 120;
    // SGR 2 (dim/faint): the foreground is pulled this far toward the background.
    private const int DimAlpha = 130;
    private const int CursorBlinkMs = 530;
    private static readonly Regex UrlRegex = new(@"https?://[^\s""'<>\)\]]+", RegexOptions.Compiled);

    private GlyphAtlas? _atlas;
    private double _dpiScale = 1.0;
    private WriteableBitmap? _bmp;
    private int[] _pix = Array.Empty<int>();
    private int _pxWidth;
    private int _pxHeight;
    private int _cols;
    private int _rows;
    private TermScreen? _screen;
    private int _scrollOffset;
    private int _lastFirstAbs;
    private Dictionary<int, List<SearchSpan>>? _searchByLine;
    private bool _selecting;
    private bool _hasSelection;
    private (int Line, int Col) _selAnchor;
    private (int Line, int Col) _selEnd;
    private List<(int Line, int Start, int End)>? _hoverLink;

    public bool ShowTermCursor;
    public event Action<int, int>? CellSizeChanged;
    private readonly System.Windows.Threading.DispatcherTimer _blinkTimer;
    private bool _blinkOn = true;
    private (int Line, int Col) _lastCursorPos = (-1, -1);

    public int Cols => _cols;
    public int RowsVisible => _rows;

    public TerminalControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        _blinkTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CursorBlinkMs) };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            if (ShowTermCursor)
            {
                RenderFrame();
            }
        };
        _blinkTimer.Start();
        Loaded += (_, _) => _blinkTimer.Start();
        Unloaded += (_, _) => _blinkTimer.Stop();
    }

    public void Attach(TermScreen screen)
    {
        _screen = screen;
        _scrollOffset = 0;
        _searchByLine = null;
        ClearSelection();
        if (_cols > 0 && _rows > 0)
        {
            CellSizeChanged?.Invoke(_cols, _rows);
        }
        RenderFrame();
    }

    public void SetSearchResults(Dictionary<int, List<SearchSpan>>? byLine)
    {
        _searchByLine = byLine;
        RenderFrame();
    }

    public void ScrollToBottom()
    {
        _scrollOffset = 0;
        RenderFrame();
    }

    public void ScrollToAbsLine(int absLine)
    {
        if (_screen == null)
        {
            return;
        }
        lock (_screen.Sync)
        {
            int total = EffectiveTotalLines();
            _scrollOffset = Math.Clamp(total - _rows - (absLine - _rows / 2), 0, Math.Max(0, total - _rows));
        }
        RenderFrame();
    }

    // Bottom-anchor the viewport to the CONTENT, not the screen grid: after a clear-screen (every restored session starts with one) the prompt sits on screen row 0 with a page of blank rows below it — anchoring to the grid would push the scrollback history out of view above. Trailing blank screen rows are ignored unless something (cursor, alt-screen app) actually uses them. Caller must hold the screen lock.
    private int EffectiveTotalLines()
    {
        var screen = _screen!;
        if (screen.OnAltScreen)
        {
            return screen.TotalLines;
        }
        int rowsUsed = screen.CursorY + 1;
        for (int r = screen.Rows - 1; r >= rowsUsed; r--)
        {
            if (screen.GetLine(screen.ScrollbackCount + r).GetText().Length > 0)
            {
                rowsUsed = r + 1;
                break;
            }
        }
        return Math.Max(screen.ScrollbackCount + rowsUsed, Math.Min(screen.TotalLines, _rows));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        try
        {
            _dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        }
        catch
        {
        }
        if (_atlas == null)
        {
            _atlas = new GlyphAtlas(_dpiScale);
            StartupTrace.Mark("glyph-atlas-built");
        }
        int pxW = (int)(ActualWidth * _dpiScale);
        int pxH = (int)(ActualHeight * _dpiScale);
        if (pxW <= 0 || pxH <= 0)
        {
            return;
        }
        int cols = Math.Max(2, pxW / _atlas.CellWidth);
        int rows = Math.Max(2, pxH / _atlas.CellHeight);
        if (pxW != _pxWidth || pxH != _pxHeight)
        {
            _pxWidth = pxW;
            _pxHeight = pxH;
            _bmp = new WriteableBitmap(pxW, pxH, 96, 96, PixelFormats.Bgra32, null);
            _pix = new int[pxW * pxH];
        }
        if (cols != _cols || rows != _rows)
        {
            _cols = cols;
            _rows = rows;
            CellSizeChanged?.Invoke(cols, rows);
        }
        RenderFrame();
    }

    public void RenderFrame()
    {
        if (_bmp == null || _atlas == null || _screen == null || _pix.Length == 0)
        {
            return;
        }
        long renderStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Array.Fill(_pix, unchecked((int)Palette.DefaultBg));
        lock (_screen.Sync)
        {
            int total = EffectiveTotalLines();
            _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, total - _rows));
            int firstAbs = total - _rows - _scrollOffset;
            _lastFirstAbs = firstAbs;
            int cursorAbs = _screen.ScrollbackCount + _screen.CursorY;
            for (int r = 0; r < _rows; r++)
            {
                int abs = firstAbs + r;
                if (abs < 0 || abs >= total)
                {
                    continue;
                }
                var line = _screen.GetLine(abs);
                List<SearchSpan>? spans = null;
                _searchByLine?.TryGetValue(abs, out spans);
                int cellCount = Math.Min(_cols, line.Cells.Length);
                for (int c = 0; c < cellCount; c++)
                {
                    DrawCell(r, c, line.Cells[c], spans, IsSelected(abs, c), IsHoveredLink(abs, c));
                }
            }
            if (ShowTermCursor && _scrollOffset == 0 && _screen.CursorVisible)
            {
                // A moving cursor resets the blink phase to visible so it never disappears mid-typing.
                var cursorPos = (cursorAbs, _screen.CursorX);
                if (cursorPos != _lastCursorPos)
                {
                    _lastCursorPos = cursorPos;
                    _blinkOn = true;
                    _blinkTimer.Stop();
                    _blinkTimer.Start();
                }
                int cursorRow = cursorAbs - firstAbs;
                if (_blinkOn && cursorRow >= 0 && cursorRow < _rows)
                {
                    DrawCursorBar(cursorRow, _screen.CursorX);
                }
            }
        }
        _bmp.WritePixels(new Int32Rect(0, 0, _pxWidth, _pxHeight), _pix, _pxWidth * 4, 0);
        InvalidateVisual();
        _frameCount++;
        _frameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - renderStart;
        if (!_firstFrameMarked)
        {
            _firstFrameMarked = true;
            StartupTrace.Mark("first-frame-painted");
        }
    }

    private bool _firstFrameMarked;
    private int _frameCount;
    private long _frameTicks;

    // Painted-frame stats since the last call: count is output-cadence-driven (idle = legitimately 0), AvgMs is what one full-bitmap render actually costs — 1000/AvgMs is the render ceiling.
    public (int Count, double AvgMs) TakeFrameStats()
    {
        int count = _frameCount;
        double avgMs = count > 0 ? _frameTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / count : 0;
        _frameCount = 0;
        _frameTicks = 0;
        return (count, avgMs);
    }

    private void DrawCursorBar(int row, int col)
    {
        var atlas = _atlas!;
        int barWidth = Math.Max(2, atlas.CellWidth / 6);
        int x0 = Math.Min(col * atlas.CellWidth, Math.Max(0, _pxWidth - barWidth));
        int y0 = row * atlas.CellHeight;
        for (int y = 0; y < atlas.CellHeight && y0 + y < _pxHeight; y++)
        {
            int rowBase = (y0 + y) * _pxWidth + x0;
            for (int x = 0; x < barWidth; x++)
            {
                _pix[rowBase + x] = unchecked((int)Palette.DefaultFg);
            }
        }
    }

    private void DrawCell(int row, int col, Cell cell, List<SearchSpan>? spans, bool selected, bool hoveredLink)
    {
        var atlas = _atlas!;
        int cw = atlas.CellWidth;
        int ch = atlas.CellHeight;
        int x0 = col * cw;
        int y0 = row * ch;
        if (x0 + cw > _pxWidth || y0 + ch > _pxHeight)
        {
            return;
        }
        uint fg = cell.Fg;
        uint bg = cell.Bg;
        bool bold = (cell.Flags & CellFlags.Bold) != 0;
        if ((cell.Flags & CellFlags.Escape) != 0)
        {
            fg = Palette.EscapeFg;
        }
        if (bold)
        {
            fg = Palette.Brighten(fg);
        }
        if ((cell.Flags & CellFlags.Dim) != 0)
        {
            fg = (uint)Blend(fg, bg, DimAlpha);
        }
        if ((cell.Flags & CellFlags.Reverse) != 0)
        {
            (fg, bg) = (bg, fg);
        }
        if (spans != null)
        {
            foreach (var span in spans)
            {
                if (col >= span.Col && col < span.Col + span.Len)
                {
                    bg = span.Current ? Palette.SearchCurrentBg : Palette.SearchBg;
                    break;
                }
            }
        }
        if (hoveredLink)
        {
            bg = Palette.LinkHoverBg;
        }
        if (selected)
        {
            bg = Palette.SelectionBg;
        }

        for (int y = 0; y < ch; y++)
        {
            int rowBase = (y0 + y) * _pxWidth + x0;
            for (int x = 0; x < cw; x++)
            {
                _pix[rowBase + x] = unchecked((int)bg);
            }
        }
        if (cell.Ch != ' ' && cell.Ch != '\0')
        {
            var alpha = atlas.Get(cell.Ch, bold);
            for (int y = 0; y < ch; y++)
            {
                int rowBase = (y0 + y) * _pxWidth + x0;
                int glyphBase = y * cw;
                for (int x = 0; x < cw; x++)
                {
                    int a = alpha[glyphBase + x];
                    if (a != 0)
                    {
                        _pix[rowBase + x] = Blend(fg, bg, a);
                    }
                }
            }
        }
        if ((cell.Flags & CellFlags.Underline) != 0)
        {
            int y = y0 + ch - 2;
            int rowBase = y * _pxWidth + x0;
            for (int x = 0; x < cw; x++)
            {
                _pix[rowBase + x] = unchecked((int)fg);
            }
        }
    }

    private static int Blend(uint fg, uint bg, int a)
    {
        if (a == 255)
        {
            return unchecked((int)fg);
        }
        int inv = 255 - a;
        int r = (int)(((fg >> 16) & 255) * (uint)a + ((bg >> 16) & 255) * (uint)inv) / 255;
        int g = (int)(((fg >> 8) & 255) * (uint)a + ((bg >> 8) & 255) * (uint)inv) / 255;
        int b = (int)((fg & 255) * (uint)a + (bg & 255) * (uint)inv) / 255;
        return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_bmp != null)
        {
            dc.DrawImage(_bmp, new Rect(0, 0, _pxWidth / _dpiScale, _pxHeight / _dpiScale));
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _scrollOffset += e.Delta / WheelDeltaPerNotch * ScrollLinesPerNotch;
        RenderFrame();
        e.Handled = true;
    }

    public bool HasSelection => _hasSelection;

    public void ClearSelection()
    {
        if (_hasSelection || _selecting)
        {
            _hasSelection = false;
            _selecting = false;
            RenderFrame();
        }
    }

    public string GetSelectedText()
    {
        if (!_hasSelection || _screen == null)
        {
            return "";
        }
        var (a, b) = OrderedSelection();
        var lines = new List<string>();
        lock (_screen.Sync)
        {
            int last = Math.Min(b.Line, _screen.TotalLines - 1);
            for (int line = Math.Max(0, a.Line); line <= last; line++)
            {
                var text = _screen.GetLine(line).GetText();
                int start = line == a.Line ? Math.Min(a.Col, text.Length) : 0;
                int end = line == b.Line ? Math.Min(b.Col + 1, text.Length) : text.Length;
                lines.Add(start < end ? text.Substring(start, end - start) : "");
            }
        }
        return string.Join("\r\n", lines);
    }

    private ((int Line, int Col) A, (int Line, int Col) B) OrderedSelection()
    {
        bool anchorFirst = _selAnchor.Line < _selEnd.Line || (_selAnchor.Line == _selEnd.Line && _selAnchor.Col <= _selEnd.Col);
        return anchorFirst ? (_selAnchor, _selEnd) : (_selEnd, _selAnchor);
    }

    private bool IsSelected(int line, int col)
    {
        if (!_hasSelection)
        {
            return false;
        }
        var (a, b) = OrderedSelection();
        if (line < a.Line || line > b.Line)
        {
            return false;
        }
        if (a.Line == b.Line)
        {
            return col >= a.Col && col <= b.Col;
        }
        if (line == a.Line)
        {
            return col >= a.Col;
        }
        if (line == b.Line)
        {
            return col <= b.Col;
        }
        return true;
    }

    private (int Line, int Col) CellAt(Point pos)
    {
        if (_atlas == null || _screen == null)
        {
            return (0, 0);
        }
        int col = Math.Clamp((int)(pos.X * _dpiScale) / _atlas.CellWidth, 0, Math.Max(0, _cols - 1));
        int row = Math.Clamp((int)(pos.Y * _dpiScale) / _atlas.CellHeight, 0, Math.Max(0, _rows - 1));
        int line;
        lock (_screen.Sync)
        {
            line = Math.Clamp(_lastFirstAbs + row, 0, Math.Max(0, _screen.TotalLines - 1));
        }
        return (line, col);
    }

    private sealed class LinkHit
    {
        public string Url = "";
        public List<(int Line, int Start, int End)> Segments = new();
    }

    // Links are found by scanning text — no escape-sequence hyperlinks needed, plain printed URLs work. Long URLs hard-wrap across rows, so the pointed-at line is joined with adjacent lines that are written edge-to-edge (the wrap signature) before matching; the returned segments map the URL back onto each visual line for highlighting.
    private const int MaxWrapJoinLines = 8;

    private LinkHit? FindLinkAt(Point pos)
    {
        if (_screen == null)
        {
            return null;
        }
        var (line, col) = CellAt(pos);
        lock (_screen.Sync)
        {
            int total = _screen.TotalLines;
            if (line >= total)
            {
                return null;
            }
            int first = line;
            while (first > 0 && line - first < MaxWrapJoinLines && LineIsFull(first - 1))
            {
                first--;
            }
            int last = line;
            while (last < total - 1 && last - line < MaxWrapJoinLines && LineIsFull(last))
            {
                last++;
            }
            var parts = new List<(int Line, int Offset, int Length)>();
            var joined = new StringBuilder();
            int clickOffset = -1;
            for (int l = first; l <= last; l++)
            {
                var text = _screen.GetLine(l).GetText();
                if (l == line)
                {
                    clickOffset = joined.Length + col;
                }
                parts.Add((l, joined.Length, text.Length));
                joined.Append(text);
            }
            var joinedText = joined.ToString();
            foreach (Match match in UrlRegex.Matches(joinedText))
            {
                var url = match.Value.TrimEnd('.', ',', ';');
                if (clickOffset >= match.Index && clickOffset < match.Index + url.Length)
                {
                    var hit = new LinkHit { Url = url };
                    foreach (var part in parts)
                    {
                        int start = Math.Max(match.Index, part.Offset);
                        int end = Math.Min(match.Index + url.Length, part.Offset + part.Length);
                        if (start < end)
                        {
                            hit.Segments.Add((part.Line, start - part.Offset, end - part.Offset));
                        }
                    }
                    return hit;
                }
            }
        }
        return null;
    }

    private bool LineIsFull(int line)
    {
        var cells = _screen!.GetLine(line);
        return cells.Cells.Length > 0 && cells.GetText().Length == cells.Cells.Length;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ChangedButton == MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && FindLinkAt(e.GetPosition(this)) is { } linkHit)
        {
            e.Handled = true;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(linkHit.Url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Write("ui", "open link failed: " + ex.Message);
            }
            return;
        }
        if (e.ChangedButton == MouseButton.Left && _screen != null)
        {
            _hasSelection = false;
            _selecting = true;
            _selAnchor = _selEnd = CellAt(e.GetPosition(this));
            CaptureMouse();
            RenderFrame();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Left && _selecting)
        {
            _selecting = false;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_screen == null || _atlas == null)
        {
            ClearTip();
            return;
        }
        if (_selecting && e.LeftButton == MouseButtonState.Pressed)
        {
            var cell = CellAt(e.GetPosition(this));
            if (cell != _selEnd)
            {
                _selEnd = cell;
                _hasSelection = cell != _selAnchor;
                RenderFrame();
            }
        }
        var pos = e.GetPosition(this);
        var hover = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? FindLinkAt(pos) : null;
        Cursor = hover != null ? Cursors.Hand : null;
        if (!SegmentsEqual(hover?.Segments, _hoverLink))
        {
            _hoverLink = hover?.Segments;
            RenderFrame();
        }
        int col = (int)(pos.X * _dpiScale) / _atlas.CellWidth;
        int row = (int)(pos.Y * _dpiScale) / _atlas.CellHeight;
        string? desc = null;
        lock (_screen.Sync)
        {
            int abs = _lastFirstAbs + row;
            if (abs >= 0 && abs < _screen.TotalLines)
            {
                var annotations = _screen.GetLine(abs).Annotations;
                if (annotations != null)
                {
                    foreach (var a in annotations)
                    {
                        if (col >= a.Start && col < a.Start + a.Length)
                        {
                            desc = a.Desc;
                            break;
                        }
                    }
                }
            }
        }
        if (desc == null)
        {
            ClearTip();
        }
        else if (!Equals(ToolTip, desc))
        {
            ToolTip = desc;
        }
    }

    private void ClearTip()
    {
        if (ToolTip != null)
        {
            ToolTip = null;
        }
    }

    private static bool SegmentsEqual(List<(int Line, int Start, int End)>? a, List<(int Line, int Start, int End)>? b)
    {
        if (a == null || b == null)
        {
            return a == b;
        }
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    private bool IsHoveredLink(int line, int col)
    {
        if (_hoverLink == null)
        {
            return false;
        }
        foreach (var segment in _hoverLink)
        {
            if (segment.Line == line && col >= segment.Start && col < segment.End)
            {
                return true;
            }
        }
        return false;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverLink != null)
        {
            _hoverLink = null;
            RenderFrame();
        }
    }
}
