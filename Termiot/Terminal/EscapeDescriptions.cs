namespace Termiot.Terminal;

public static class EscapeDescriptions
{
    public static string Csi(char final, char prefix, int[] p)
    {
        if (prefix == '?' && (final == 'h' || final == 'l'))
        {
            string action = final == 'h' ? "enable" : "disable";
            string what = p.Length > 0 ? PrivateModeName(p[0]) : "mode";
            return $"DEC private mode — {action} {what}";
        }
        return final switch
        {
            'A' => "CUU — cursor up",
            'B' => "CUD — cursor down",
            'C' => "CUF — cursor forward",
            'D' => "CUB — cursor back",
            'E' => "CNL — cursor to next line",
            'F' => "CPL — cursor to previous line",
            'G' or '`' => "CHA — cursor to column",
            'H' or 'f' => "CUP — cursor position",
            'J' => "ED — erase in display",
            'K' => "EL — erase in line",
            'L' => "IL — insert lines",
            'M' => "DL — delete lines",
            'P' => "DCH — delete characters",
            '@' => "ICH — insert characters",
            'X' => "ECH — erase characters",
            'S' => "SU — scroll up",
            'T' => "SD — scroll down",
            'd' => "VPA — cursor to row",
            'r' => "DECSTBM — set scrolling margins",
            's' => "Save cursor position",
            'u' => "Restore cursor position",
            'm' => "SGR — set colors and attributes",
            'n' => "DSR — device status report (query)",
            'c' => "DA — device attributes (query)",
            't' => "Window manipulation",
            'q' => "DECSCUSR — set cursor style",
            _ => "CSI sequence",
        };
    }

    private static string PrivateModeName(int mode)
    {
        return mode switch
        {
            12 => "cursor blinking",
            25 => "cursor visibility",
            47 or 1047 => "alternate screen buffer",
            1048 => "cursor save/restore",
            1049 => "alternate screen buffer (with cursor save)",
            1004 => "focus reporting",
            2004 => "bracketed paste",
            9001 => "win32 input mode",
            _ => $"mode {mode}",
        };
    }

    public static string Osc(int code)
    {
        return code switch
        {
            0 => "OSC 0 — set window title and icon",
            1 => "OSC 1 — set icon name",
            2 => "OSC 2 — set window title",
            4 => "OSC 4 — set palette color",
            7 => "OSC 7 — report working directory",
            8 => "OSC 8 — hyperlink",
            9 => "OSC 9 — notification / progress",
            10 => "OSC 10 — set default foreground color",
            11 => "OSC 11 — set default background color",
            52 => "OSC 52 — clipboard access",
            133 => "OSC 133 — shell integration marks",
            633 => "OSC 633 — VS Code shell integration",
            _ => $"OSC {code}",
        };
    }
}
