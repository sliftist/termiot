using System.Text;

namespace Termiot.Terminal;

// Byte stream → screen operations. When ShowEscapes is on, ESC-initiated sequences are printed literally (control chars mapped to U+24xx symbols) with a tooltip annotation instead of being executed; query sequences (DSR/DA) still get responses so the shell never hangs waiting for one.
public sealed class VtParser
{
    private const int DecodeBufferChars = 16 * 1024;
    private const int MaxCsiParamValue = 32767;
    private const int MaxSequenceChars = 4096;

    private enum State
    {
        Ground,
        Escape,
        Charset,
        Csi,
        Osc,
        OscEsc,
        Str,
        StrEsc,
    }

    private readonly TermScreen _s;
    public bool ShowEscapes;
    public Action<string>? OnTitle;
    public Action<byte[]>? OnRespond;
    // Fired when a termiot command marker (APC termiot-cmd:<base64>) is encountered — during live output and during log replay alike, so command/output structure survives restarts.
    public Action<string>? OnCommandMarker;
    // ConPTY requests win32-input-mode (?9001) at session start; when granted, keyboard input is sent as full Win32 key records so modifier distinctions (Ctrl+Enter vs Enter) survive.
    public Action<bool>? OnWin32InputMode;

    private const string CommandMarkerPrefix = "termiot-cmd:";

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _decoded = new char[DecodeBufferChars];
    private State _state = State.Ground;
    private readonly StringBuilder _raw = new();
    private readonly StringBuilder _csiParams = new();
    private char _csiPrefix;
    private char _strIntroducer;
    private readonly StringBuilder _osc = new();
    // Rolling window of recently printed text, kept as implementation context for recorded unhandled sequences.
    private const int ContextChars = 80;
    private readonly char[] _recentText = new char[ContextChars];
    private int _recentPos;

    public VtParser(TermScreen screen)
    {
        _s = screen;
    }

    public void Feed(byte[] buf, int offset, int count)
    {
        while (count > 0)
        {
            _decoder.Convert(buf, offset, count, _decoded, 0, _decoded.Length, false, out int bytesUsed, out int charsUsed, out _);
            offset += bytesUsed;
            count -= bytesUsed;
            for (int i = 0; i < charsUsed; i++)
            {
                Process(_decoded[i]);
            }
        }
    }

    private void Process(char c)
    {
        if (_state != State.Ground)
        {
            if (_raw.Length >= MaxSequenceChars)
            {
                _raw.Clear();
                _osc.Clear();
                _state = State.Ground;
                return;
            }
            _raw.Append(c);
        }
        switch (_state)
        {
            case State.Ground:
                ProcessGround(c);
                break;
            case State.Escape:
                ProcessEscape(c);
                break;
            case State.Charset:
                RecordUnhandled($"ESC charset {c}");
                Finish(null, "Designate character set");
                break;
            case State.Csi:
                ProcessCsi(c);
                break;
            case State.Osc:
                if (c == '\a')
                {
                    DispatchOsc();
                }
                else if (c == '\x1b')
                {
                    _state = State.OscEsc;
                }
                else
                {
                    _osc.Append(c);
                }
                break;
            case State.OscEsc:
                if (c == '\\')
                {
                    DispatchOsc();
                }
                else
                {
                    _osc.Append('\x1b').Append(c);
                    _state = State.Osc;
                }
                break;
            case State.Str:
                if (c == '\a')
                {
                    DispatchStr();
                }
                else if (c == '\x1b')
                {
                    _state = State.StrEsc;
                }
                else
                {
                    _osc.Append(c);
                }
                break;
            case State.StrEsc:
                if (c == '\\')
                {
                    DispatchStr();
                }
                else
                {
                    _osc.Append('\x1b').Append(c);
                    _state = State.Str;
                }
                break;
        }
    }

    private void ProcessGround(char c)
    {
        switch (c)
        {
            case '\x1b':
                _raw.Clear();
                _raw.Append(c);
                _state = State.Escape;
                break;
            case '\r':
                _s.CarriageReturn();
                break;
            case '\n':
            case '\v':
            case '\f':
                _s.LineFeed();
                break;
            case '\b':
                _s.Backspace();
                break;
            case '\t':
                _s.HorizontalTab();
                break;
            case '\a':
                break;
            default:
                if (c >= ' ')
                {
                    _s.Print(c);
                    _recentText[_recentPos++ % ContextChars] = c;
                }
                break;
        }
    }

    private void RecordUnhandled(string key)
    {
        var context = new StringBuilder(ContextChars);
        for (int i = 0; i < ContextChars; i++)
        {
            var c = _recentText[(_recentPos + i) % ContextChars];
            if (c != '\0')
            {
                context.Append(c);
            }
        }
        EscapeRecorder.Record(key, Visualize(_raw), context.ToString());
    }

    private void ProcessEscape(char c)
    {
        switch (c)
        {
            case '[':
                _csiParams.Clear();
                _csiPrefix = '\0';
                _state = State.Csi;
                break;
            case ']':
                _osc.Clear();
                _state = State.Osc;
                break;
            case 'P':
            case 'X':
            case '^':
            case '_':
                _strIntroducer = c;
                _osc.Clear();
                _state = State.Str;
                break;
            case '(':
            case ')':
            case '*':
            case '+':
                _state = State.Charset;
                break;
            case '7':
                Finish(_s.SaveCursor, "DECSC — save cursor position and attributes");
                break;
            case '8':
                Finish(_s.RestoreCursor, "DECRC — restore cursor position and attributes");
                break;
            case 'M':
                Finish(_s.ReverseLineFeed, "RI — reverse line feed");
                break;
            case 'D':
                Finish(_s.LineFeed, "IND — line feed");
                break;
            case 'E':
                Finish(() =>
                {
                    _s.CarriageReturn();
                    _s.LineFeed();
                }, "NEL — next line");
                break;
            case 'c':
                Finish(_s.FullReset, "RIS — full terminal reset");
                break;
            case '=':
            case '>':
                Finish(null, "Keypad mode");
                break;
            default:
                RecordUnhandled($"ESC {c}");
                Finish(null, "Escape sequence");
                break;
        }
    }

    private void ProcessCsi(char c)
    {
        if ((c >= '0' && c <= '9') || c == ';' || c == ':')
        {
            _csiParams.Append(c);
        }
        else if (c == '?' || c == '>' || c == '<' || c == '=')
        {
            _csiPrefix = c;
        }
        else if (c >= ' ' && c <= '/')
        {
            // Intermediate bytes (e.g. DECSCUSR's space) — the sequences we support don't distinguish on them.
        }
        else if (c >= '@' && c <= '~')
        {
            DispatchCsi(c);
        }
        else if (c == '\r' || c == '\n' || c == '\b' || c == '\t')
        {
            ProcessGround(c);
        }
        else
        {
            _state = State.Ground;
        }
    }

    private void Finish(Action? execute, string desc)
    {
        if (ShowEscapes)
        {
            _s.PrintEscapeText(Visualize(_raw), desc);
        }
        else
        {
            execute?.Invoke();
        }
        _state = State.Ground;
    }

    private void DispatchCsi(char final)
    {
        var p = ParseParams();
        Respond(final, p);
        Finish(() => Execute(final, p), EscapeDescriptions.Csi(final, _csiPrefix, p));
    }

    private int[] ParseParams()
    {
        var parts = _csiParams.ToString().Split(';');
        var p = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            int colon = part.IndexOf(':');
            if (colon >= 0)
            {
                part = part.Substring(0, colon);
            }
            if (int.TryParse(part, out int v))
            {
                p[i] = Math.Clamp(v, 0, MaxCsiParamValue);
            }
        }
        return p;
    }

    private static int Param(int[] p, int index, int fallback)
    {
        return index < p.Length && p[index] != 0 ? p[index] : fallback;
    }

    private void Respond(char final, int[] p)
    {
        if (_csiPrefix != '\0' && final != 'c')
        {
            return;
        }
        string? response = null;
        if (final == 'n' && Param(p, 0, 0) == 6)
        {
            response = $"\x1b[{_s.CursorY + 1};{_s.CursorX + 1}R";
        }
        else if (final == 'n' && Param(p, 0, 0) == 5)
        {
            response = "\x1b[0n";
        }
        else if (final == 'c')
        {
            response = "\x1b[?1;0c";
        }
        if (response != null)
        {
            OnRespond?.Invoke(Encoding.ASCII.GetBytes(response));
        }
    }

    private void Execute(char final, int[] p)
    {
        int n = Param(p, 0, 1);
        switch (final)
        {
            case 'A':
                _s.MoveCursor(-n, 0);
                break;
            case 'B':
            case 'e':
                _s.MoveCursor(n, 0);
                break;
            case 'C':
            case 'a':
                _s.MoveCursor(0, n);
                break;
            case 'D':
                _s.MoveCursor(0, -n);
                break;
            case 'E':
                _s.CarriageReturn();
                _s.MoveCursor(n, 0);
                break;
            case 'F':
                _s.CarriageReturn();
                _s.MoveCursor(-n, 0);
                break;
            case 'G':
            case '`':
                _s.SetCursorCol(n - 1);
                break;
            case 'd':
                _s.SetCursorRow(n - 1);
                break;
            case 'H':
            case 'f':
                _s.SetCursorPos(Param(p, 0, 1) - 1, Param(p, 1, 1) - 1);
                break;
            case 'J':
                _s.EraseInDisplay(p.Length > 0 ? p[0] : 0);
                break;
            case 'K':
                _s.EraseInLine(p.Length > 0 ? p[0] : 0);
                break;
            case 'L':
                _s.InsertLines(n);
                break;
            case 'M':
                _s.DeleteLines(n);
                break;
            case 'P':
                _s.DeleteChars(n);
                break;
            case '@':
                _s.InsertChars(n);
                break;
            case 'X':
                _s.EraseChars(n);
                break;
            case 'S':
                _s.ScrollUp(n);
                break;
            case 'T':
                _s.ScrollDown(n);
                break;
            case 'r':
                _s.SetMargins(Param(p, 0, 1) - 1, Param(p, 1, _s.Rows) - 1);
                break;
            case 's':
                _s.SaveCursor();
                break;
            case 'u':
                _s.RestoreCursor();
                break;
            case 'm':
                Sgr(p);
                break;
            case 'h':
            case 'l':
                if (_csiPrefix == '?')
                {
                    foreach (var mode in p)
                    {
                        SetPrivateMode(mode, final == 'h');
                    }
                }
                else
                {
                    RecordUnhandled($"CSI {_csiParams} {final}");
                }
                break;
            default:
                RecordUnhandled(NormalizedCsiKey(final));
                break;
        }
    }

    // Positional numeric parameters are collapsed to "n" so counts and coordinates don't create one entry per value; mode-identifying parameters (h/l) keep their numbers via the dedicated call sites.
    private string NormalizedCsiKey(char final)
    {
        var sb = new StringBuilder("CSI ");
        if (_csiPrefix != '\0')
        {
            sb.Append(_csiPrefix);
        }
        bool inNumber = false;
        foreach (var c in _csiParams.ToString())
        {
            if (c >= '0' && c <= '9')
            {
                if (!inNumber)
                {
                    sb.Append('n');
                    inNumber = true;
                }
            }
            else
            {
                inNumber = false;
                sb.Append(c);
            }
        }
        sb.Append(' ').Append(final);
        return sb.ToString();
    }

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 25:
                _s.CursorVisible = on;
                break;
            case 9001:
                OnWin32InputMode?.Invoke(on);
                break;
            case 47:
                _s.UseAltScreen(on, false);
                break;
            case 1047:
                _s.UseAltScreen(on, on);
                break;
            case 1048:
                if (on)
                {
                    _s.SaveCursor();
                }
                else
                {
                    _s.RestoreCursor();
                }
                break;
            case 1049:
                if (on)
                {
                    _s.SaveCursor();
                    _s.UseAltScreen(true, true);
                }
                else
                {
                    _s.UseAltScreen(false, false);
                    _s.RestoreCursor();
                }
                break;
            default:
                RecordUnhandled($"CSI ?{mode} {(on ? 'h' : 'l')}");
                break;
        }
    }

    private void Sgr(int[] p)
    {
        if (p.Length == 0)
        {
            p = new[] { 0 };
        }
        for (int i = 0; i < p.Length; i++)
        {
            int v = p[i];
            switch (v)
            {
                case 0:
                    _s.CurFg = Palette.DefaultFg;
                    _s.CurBg = Palette.DefaultBg;
                    _s.CurFlags = CellFlags.None;
                    break;
                case 1:
                    _s.CurFlags |= CellFlags.Bold;
                    break;
                case 2:
                    _s.CurFlags |= CellFlags.Dim;
                    break;
                case 4:
                    _s.CurFlags |= CellFlags.Underline;
                    break;
                case 7:
                    _s.CurFlags |= CellFlags.Reverse;
                    break;
                case 22:
                    _s.CurFlags &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;
                case 24:
                    _s.CurFlags &= ~CellFlags.Underline;
                    break;
                case 27:
                    _s.CurFlags &= ~CellFlags.Reverse;
                    break;
                case >= 30 and <= 37:
                    _s.CurFg = Palette.Color16(v - 30);
                    break;
                case 38:
                    i += ReadExtendedColor(p, i, out var fg);
                    if (fg is { } f)
                    {
                        _s.CurFg = f;
                    }
                    break;
                case 39:
                    _s.CurFg = Palette.DefaultFg;
                    break;
                case >= 40 and <= 47:
                    _s.CurBg = Palette.Color16(v - 40);
                    break;
                case 48:
                    i += ReadExtendedColor(p, i, out var bg);
                    if (bg is { } b)
                    {
                        _s.CurBg = b;
                    }
                    break;
                case 49:
                    _s.CurBg = Palette.DefaultBg;
                    break;
                case >= 90 and <= 97:
                    _s.CurFg = Palette.Color16(v - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    _s.CurBg = Palette.Color16(v - 100 + 8);
                    break;
                default:
                    RecordUnhandled($"SGR {v}");
                    break;
            }
        }
    }

    private static int ReadExtendedColor(int[] p, int i, out uint? color)
    {
        color = null;
        if (i + 1 >= p.Length)
        {
            return 0;
        }
        if (p[i + 1] == 5 && i + 2 < p.Length)
        {
            color = Palette.Color256(p[i + 2]);
            return 2;
        }
        if (p[i + 1] == 2 && i + 4 < p.Length)
        {
            color = Palette.Rgb(p[i + 2], p[i + 3], p[i + 4]);
            return 4;
        }
        return 1;
    }

    private void DispatchStr()
    {
        var body = _osc.ToString();
        if (body.StartsWith(CommandMarkerPrefix, StringComparison.Ordinal))
        {
            try
            {
                var command = Encoding.UTF8.GetString(Convert.FromBase64String(body[CommandMarkerPrefix.Length..]));
                if (command.Length > 0)
                {
                    OnCommandMarker?.Invoke(command);
                }
            }
            catch
            {
            }
            _state = State.Ground;
            return;
        }
        RecordUnhandled(_strIntroducer switch
        {
            'P' => "DCS string",
            'X' => "SOS string",
            '^' => "PM string",
            _ => "APC string",
        });
        Finish(null, "Control string");
    }

    private void DispatchOsc()
    {
        string body = _osc.ToString();
        int code = 0;
        int semi = body.IndexOf(';');
        string arg = "";
        if (semi >= 0)
        {
            int.TryParse(body.Substring(0, semi), out code);
            arg = body.Substring(semi + 1);
        }
        else
        {
            int.TryParse(body, out code);
        }
        if (code is not (0 or 1 or 2))
        {
            RecordUnhandled($"OSC {code}");
        }
        if (ShowEscapes)
        {
            _s.PrintEscapeText(Visualize(_raw), EscapeDescriptions.Osc(code));
        }
        else if (code is 0 or 1 or 2)
        {
            OnTitle?.Invoke(arg);
        }
        _state = State.Ground;
    }

    private static string Visualize(StringBuilder raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c < ' ')
            {
                sb.Append((char)(0x2400 + c));
            }
            else if (c == '\x7f')
            {
                sb.Append('␡');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
