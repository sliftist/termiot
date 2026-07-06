using Microsoft.Win32;

namespace Termiot;

// Windows "default terminal application" management via the delegation registry values (HKCU only — no elevation needed). Making us default = registering this exe as a per-user COM local server for the Termiot handoff CLSID and pointing HKCU\Console\%%Startup at it; conhost then CoCreates us and hands new console sessions over via ITerminalHandoff3.
public static class DefaultTerminal
{
    public const string TermiotClsid = "{9F0E7E3B-6A0C-4B6B-9D0F-2C5A9E31D8A4}";
    private const string StartupKeyPath = @"Console\%%Startup";
    // The inbox Windows console host, used for the console (server) side of the delegation pair — we only replace the terminal (UX) side.
    private const string ConhostConsoleClsid = "{B23D10C0-E52E-411E-9D5B-C09FDF709C7D}";
    private const string WindowsTerminalClsid = "{E12CFF52-A866-4C77-9A90-F570A7AA2C6B}";
    private const string LetWindowsDecide = "{00000000-0000-0000-0000-000000000000}";

    public sealed class State
    {
        public string TerminalClsid = "";
        public string Description = "";
        public string ServerPath = "";
        public bool IsThisExe;
        public bool IsTermiot;
    }

    public static State GetState()
    {
        var state = new State();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath);
            state.TerminalClsid = key?.GetValue("DelegationTerminal") as string ?? "";
        }
        catch
        {
        }
        if (state.TerminalClsid.Length == 0 || string.Equals(state.TerminalClsid, LetWindowsDecide, StringComparison.OrdinalIgnoreCase))
        {
            state.Description = "Let Windows decide (Windows default)";
            return state;
        }
        if (string.Equals(state.TerminalClsid, ConhostConsoleClsid, StringComparison.OrdinalIgnoreCase))
        {
            state.Description = "Windows Console Host";
            return state;
        }
        if (string.Equals(state.TerminalClsid, WindowsTerminalClsid, StringComparison.OrdinalIgnoreCase))
        {
            state.Description = "Windows Terminal";
            return state;
        }
        state.ServerPath = ResolveLocalServerPath(state.TerminalClsid);
        state.IsTermiot = string.Equals(state.TerminalClsid, TermiotClsid, StringComparison.OrdinalIgnoreCase);
        var ownPath = Environment.ProcessPath ?? "";
        state.IsThisExe = state.IsTermiot && string.Equals(ExtractExePath(state.ServerPath), ownPath, StringComparison.OrdinalIgnoreCase);
        state.Description = state.IsTermiot ? (state.IsThisExe ? "Termiot (this instance's executable)" : "Termiot (a DIFFERENT executable copy)") : "Unknown terminal";
        return state;
    }

    private static string ResolveLocalServerPath(string clsid)
    {
        try
        {
            using var user = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{clsid}\LocalServer32");
            if (user?.GetValue("") is string userPath && userPath.Length > 0)
            {
                return userPath;
            }
            using var machine = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\LocalServer32");
            return machine?.GetValue("") as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string ExtractExePath(string localServerCommand)
    {
        var command = localServerCommand.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 0 ? command[1..end] : command.Trim('"');
        }
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    public static void MakeDefault()
    {
        var exe = Environment.ProcessPath!;
        using (var clsidKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{TermiotClsid}"))
        {
            clsidKey.SetValue("", "Termiot terminal handoff");
            using var server = clsidKey.CreateSubKey("LocalServer32");
            server.SetValue("", $"\"{exe}\" -Embedding");
        }
        using var startup = Registry.CurrentUser.CreateSubKey(StartupKeyPath);
        startup.SetValue("DelegationConsole", ConhostConsoleClsid);
        startup.SetValue("DelegationTerminal", TermiotClsid);
        AppLog.Write("defterm", $"registered as default terminal, server = {exe}");
    }

    public static void ResetDefault()
    {
        using var startup = Registry.CurrentUser.CreateSubKey(StartupKeyPath);
        startup.SetValue("DelegationConsole", LetWindowsDecide);
        startup.SetValue("DelegationTerminal", LetWindowsDecide);
        AppLog.Write("defterm", "delegation reset to Windows default");
    }
}
