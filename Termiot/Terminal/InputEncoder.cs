using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace Termiot.Terminal;

// Raw-keystroke mode: WPF key events → VT input sequences. Plain printable characters are not handled here — they arrive via TextInput and are sent as-is. When the shell has enabled win32-input-mode, the EncodeWin32* functions produce full key records (ESC[Vk;Sc;Uc;Kd;Cs;Rc_) instead, which preserve modifier state that classic VT cannot express (Ctrl+Enter vs Shift+Enter vs Enter).
public static class InputEncoder
{
    private const int SHIFT_PRESSED = 0x10;
    private const int LEFT_CTRL_PRESSED = 0x08;
    private const int LEFT_ALT_PRESSED = 0x02;
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

    // Win32-input-mode record for a special or modified key; null for plain printable keys (those arrive via TextInput → EncodeWin32Text). Down and up records are sent as a pair.
    public static byte[]? EncodeWin32Key(Key key, ModifierKeys mods)
    {
        bool special = key is Key.Enter or Key.Tab or Key.Back or Key.Escape or Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Insert or Key.Delete or (>= Key.F1 and <= Key.F24);
        bool modified = (mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0;
        if (!special && !modified)
        {
            return null;
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            return null;
        }
        uint sc = MapVirtualKey((uint)vk, 0);
        // The char field must match what Windows itself would put in the KEY_EVENT_RECORD — apps key off it: Ctrl+Enter carries \n (not \r, which means submit), Ctrl+Backspace carries DEL, Ctrl+letter the control char, Alt+letter the letter itself (conhost turns that into ESC <letter>). Shift+Enter deliberately mimics Ctrl+Enter — most terminals don't bother, but shift is the easier chord and apps like Claude Code treat \n as insert-newline.
        int uc = key switch
        {
            Key.Enter => (mods & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 ? 10 : 13,
            Key.Tab => 9,
            Key.Back => (mods & ModifierKeys.Control) != 0 ? 127 : 8,
            Key.Escape => 27,
            _ => 0,
        };
        if (uc == 0 && (mods & ModifierKeys.Control) != 0 && key >= Key.A && key <= Key.Z)
        {
            uc = key - Key.A + 1;
        }
        else if (uc == 0 && (mods & ModifierKeys.Alt) != 0 && (mods & ModifierKeys.Control) == 0)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                uc = ((mods & ModifierKeys.Shift) != 0 ? 'A' : 'a') + (key - Key.A);
            }
            else if (key >= Key.D0 && key <= Key.D9)
            {
                uc = '0' + (key - Key.D0);
            }
        }
        int cs = ControlState(mods);
        return Encoding.UTF8.GetBytes(Record(vk, sc, uc, true, cs) + Record(vk, sc, uc, false, cs));
    }

    public static byte[] EncodeWin32Text(string text)
    {
        var sb = new StringBuilder(text.Length * 24);
        foreach (var c in text)
        {
            sb.Append(Record(0, 0, c, true, 0)).Append(Record(0, 0, c, false, 0));
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Record(int vk, uint sc, int uc, bool down, int controlState)
    {
        return $"\x1b[{vk};{sc};{uc};{(down ? 1 : 0)};{controlState};1_";
    }

    private static int ControlState(ModifierKeys mods)
    {
        int cs = 0;
        if ((mods & ModifierKeys.Shift) != 0)
        {
            cs |= SHIFT_PRESSED;
        }
        if ((mods & ModifierKeys.Control) != 0)
        {
            cs |= LEFT_CTRL_PRESSED;
        }
        if ((mods & ModifierKeys.Alt) != 0)
        {
            cs |= LEFT_ALT_PRESSED;
        }
        return cs;
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
