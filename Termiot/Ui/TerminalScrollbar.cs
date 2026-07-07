using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Termiot.Ui;

// The terminal's scrollbar, laid out in its own gutter beside the TerminalControl (not overlapping the text). Reads scroll state from the attached terminal and is repainted whenever the terminal repaints. All lines are the same height, so a line's position on the track is simply abs/total.
public sealed class TerminalScrollbar : FrameworkElement
{
    private const double MinThumbHeight = 20;
    private const double MatchTickHeight = 2.0;
    private static readonly SolidColorBrush TrackBrush = FrozenBrush(0x22, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush ThumbBrush = FrozenBrush(0x66, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush MatchBrush = FrozenBrush(0xFF, 0xE8, 0xA0, 0x30);

    private TerminalControl? _term;
    private bool _dragging;

    public void Attach(TerminalControl term)
    {
        _term = term;
        term.Scrollbar = this;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var term = _term;
        if (term == null)
        {
            return;
        }
        int total = term.ScrollTotal;
        int rows = term.RowsVisible;
        double h = ActualHeight;
        double w = ActualWidth;
        if (total <= rows || rows <= 0 || h < 1 || w < 1)
        {
            return;
        }
        dc.DrawRectangle(TrackBrush, null, new Rect(0, 0, w, h));
        if (term.SearchMatchLines is { } matches)
        {
            foreach (var abs in matches)
            {
                double ty = (double)abs / total * h;
                dc.DrawRectangle(MatchBrush, null, new Rect(0, Math.Min(ty, h - MatchTickHeight), w, MatchTickHeight));
            }
        }
        double thumbH = Math.Max(MinThumbHeight, (double)rows / total * h);
        double thumbY = Math.Clamp((double)term.ScrollFirstAbs / total * h, 0, h - thumbH);
        dc.DrawRectangle(ThumbBrush, null, new Rect(0, thumbY, w, thumbH));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_term == null)
        {
            return;
        }
        _dragging = true;
        CaptureMouse();
        _term.ScrollToFraction(e.GetPosition(this).Y / Math.Max(1, ActualHeight));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging && _term != null && e.LeftButton == MouseButtonState.Pressed)
        {
            _term.ScrollToFraction(e.GetPosition(this).Y / Math.Max(1, ActualHeight));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
        }
    }

    private static SolidColorBrush FrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
