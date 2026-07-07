using System.Text;
using System.Windows.Input;

namespace Termiot;

public sealed record HotkeyDef(string Id, string Label, string DefaultGesture);

// Rebindable window shortcuts. Settings stores only overrides (id → gesture string like "Ctrl+Shift+Tab"); anything not overridden uses the default. Matching is exact on modifiers, so overlapping combos (Ctrl+Tab vs Ctrl+Shift+Tab) can't shadow each other.
public static class Hotkeys
{
    public static readonly HotkeyDef[] All =
    {
        new("new-tab", "New tab (in the current tab's directory)", "Ctrl+T"),
        new("close-tab", "Close tab", "Ctrl+W"),
        new("next-tab", "Next tab", "Ctrl+Tab"),
        new("prev-tab", "Previous tab", "Ctrl+Shift+Tab"),
        new("new-window", "New window", "Ctrl+N"),
        new("search", "Search output", "Ctrl+F"),
        new("restart-shell", "Restart the current shell", "Alt+Pause"),
    };

    public static string GestureFor(AppSettings settings, string id)
    {
        if (settings.Hotkeys.TryGetValue(id, out var gesture) && gesture.Length > 0)
        {
            return gesture;
        }
        return All.First(d => d.Id == id).DefaultGesture;
    }

    public static bool Matches(AppSettings settings, string id, Key key, ModifierKeys mods)
    {
        return TryParse(GestureFor(settings, id), out var boundKey, out var boundMods) && boundKey == key && boundMods == mods;
    }

    public static bool TryParse(string gesture, out Key key, out ModifierKeys mods)
    {
        key = Key.None;
        mods = ModifierKeys.None;
        foreach (var raw in gesture.Split('+'))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= ModifierKeys.Control;
                    continue;
                case "shift":
                    mods |= ModifierKeys.Shift;
                    continue;
                case "alt":
                    mods |= ModifierKeys.Alt;
                    continue;
                case "win":
                    mods |= ModifierKeys.Windows;
                    continue;
            }
            if (!Enum.TryParse(part, ignoreCase: true, out key))
            {
                return false;
            }
        }
        return key != Key.None;
    }

    public static string Format(Key key, ModifierKeys mods)
    {
        var sb = new StringBuilder();
        if ((mods & ModifierKeys.Control) != 0)
        {
            sb.Append("Ctrl+");
        }
        if ((mods & ModifierKeys.Shift) != 0)
        {
            sb.Append("Shift+");
        }
        if ((mods & ModifierKeys.Alt) != 0)
        {
            sb.Append("Alt+");
        }
        if ((mods & ModifierKeys.Windows) != 0)
        {
            sb.Append("Win+");
        }
        sb.Append(key);
        return sb.ToString();
    }

    public static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;
    }
}
