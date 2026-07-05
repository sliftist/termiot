using System.Text;
using System.Windows.Input;

namespace Termiot.Terminal;

// Raw-keystroke mode: WPF key events → VT input sequences. Plain printable characters are not handled here — they arrive via TextInput and are sent as-is.
public static class InputEncoder
{
    public static byte[]? Encode(Key key, ModifierKeys mods)
    {
        string? s = key switch
        {
            Key.Enter => "\r",
            Key.Tab => "\t",
            Key.Back => "\x7f",
            Key.Escape => "\x1b",
            Key.Up => "\x1b[A",
            Key.Down => "\x1b[B",
            Key.Right => "\x1b[C",
            Key.Left => "\x1b[D",
            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.F1 => "\x1bOP",
            Key.F2 => "\x1bOQ",
            Key.F3 => "\x1bOR",
            Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~",
            Key.F6 => "\x1b[17~",
            Key.F7 => "\x1b[18~",
            Key.F8 => "\x1b[19~",
            Key.F9 => "\x1b[20~",
            Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~",
            Key.F12 => "\x1b[24~",
            _ => null,
        };
        if (s == null && (mods & ModifierKeys.Control) != 0 && (mods & ModifierKeys.Alt) == 0)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                s = ((char)(key - Key.A + 1)).ToString();
            }
            else if (key == Key.Space)
            {
                s = "\0";
            }
        }
        return s == null ? null : Encoding.UTF8.GetBytes(s);
    }
}
