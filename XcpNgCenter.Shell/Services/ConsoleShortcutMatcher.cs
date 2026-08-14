using Avalonia.Input;

namespace XcpNgCenter.Shell.Services;

public static class ConsoleShortcutMatcher
{
    public static bool Matches(KeyEventArgs e, string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)
            || string.Equals(shortcut, "None", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var modifiers = e.KeyModifiers;
        return shortcut switch
        {
            "Ctrl+Enter" => e.Key == Key.Enter && modifiers.HasFlag(KeyModifiers.Control),
            "Ctrl+Alt+F" => e.Key == Key.F
                            && modifiers.HasFlag(KeyModifiers.Control)
                            && modifiers.HasFlag(KeyModifiers.Alt),
            "F12" => e.Key == Key.F12,
            "Ctrl+Alt" => IsControlKey(e.Key) && modifiers.HasFlag(KeyModifiers.Alt)
                          || IsAltKey(e.Key) && modifiers.HasFlag(KeyModifiers.Control),
            "Alt+Shift+U" => e.Key == Key.U
                             && modifiers.HasFlag(KeyModifiers.Alt)
                             && modifiers.HasFlag(KeyModifiers.Shift),
            "F11" => e.Key == Key.F11,
            _ => false
        };
    }

    private static bool IsControlKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl;

    private static bool IsAltKey(Key key) => key is Key.LeftAlt or Key.RightAlt;
}
