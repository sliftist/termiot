using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Termiot.Ui;

// The floating box that follows the cursor while a tab is dragged. Marked WS_EX_TRANSPARENT so it never intercepts the drag's hit testing — without that, the ghost itself would become the drop target.
public sealed class DragGhost : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const double CursorOffsetDips = 14;

    public DragGhost(string title)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x2A, 0x2A, 0x2A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 5, 12, 5),
            Child = new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                MaxWidth = 300,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
    }

    public void MoveToCursor(double dpiScale)
    {
        GetCursorPos(out var point);
        Left = point.X / dpiScale + CursorOffsetDips;
        Top = point.Y / dpiScale + CursorOffsetDips;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
