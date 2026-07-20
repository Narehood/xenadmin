using Avalonia.Input;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Maps Avalonia keys to RFB/X11 keysyms for hosted console input.
/// Prefers <see cref="KeyEventArgs.KeySymbol"/> for printable characters (layout-aware),
/// with an explicit special-key table for modifiers/navigation/function keys.
/// </summary>
public static class RfbKeySym
{
    public static int FromKeyEvent(KeyEventArgs e)
    {
        var special = MapSpecial(e.Key);
        if (special > 0)
            return special;

        // Layout-aware printable characters when Ctrl/Alt are not held.
        var ctrlOrAlt = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                        || e.KeyModifiers.HasFlag(KeyModifiers.Alt)
                        || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!ctrlOrAlt
            && !string.IsNullOrEmpty(e.KeySymbol)
            && e.KeySymbol.Length == 1)
        {
            var ch = e.KeySymbol[0];
            if (ch >= 0x20 && ch != 0x7f)
                return ch;
        }

        return FromKey(e.Key, e.KeyModifiers);
    }

    public static int FromKey(Key key, KeyModifiers modifiers)
    {
        var special = MapSpecial(key);
        if (special > 0)
            return special;

        if (key is >= Key.A and <= Key.Z)
        {
            var baseChar = (char)('a' + (key - Key.A));
            var shifted = modifiers.HasFlag(KeyModifiers.Shift);
            return shifted ? char.ToUpperInvariant(baseChar) : baseChar;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            if (!modifiers.HasFlag(KeyModifiers.Shift))
                return '0' + (key - Key.D0);

            return key switch
            {
                Key.D1 => '!',
                Key.D2 => '@',
                Key.D3 => '#',
                Key.D4 => '$',
                Key.D5 => '%',
                Key.D6 => '^',
                Key.D7 => '&',
                Key.D8 => '*',
                Key.D9 => '(',
                Key.D0 => ')',
                _ => -1
            };
        }

        return key switch
        {
            Key.OemMinus => modifiers.HasFlag(KeyModifiers.Shift) ? '_' : '-',
            Key.OemPlus => modifiers.HasFlag(KeyModifiers.Shift) ? '+' : '=',
            Key.OemOpenBrackets => modifiers.HasFlag(KeyModifiers.Shift) ? '{' : '[',
            Key.OemCloseBrackets => modifiers.HasFlag(KeyModifiers.Shift) ? '}' : ']',
            Key.OemPipe => modifiers.HasFlag(KeyModifiers.Shift) ? '|' : '\\',
            Key.OemSemicolon => modifiers.HasFlag(KeyModifiers.Shift) ? ':' : ';',
            Key.OemQuotes => modifiers.HasFlag(KeyModifiers.Shift) ? '"' : '\'',
            Key.OemComma => modifiers.HasFlag(KeyModifiers.Shift) ? '<' : ',',
            Key.OemPeriod => modifiers.HasFlag(KeyModifiers.Shift) ? '>' : '.',
            Key.OemQuestion => modifiers.HasFlag(KeyModifiers.Shift) ? '?' : '/',
            Key.OemTilde => modifiers.HasFlag(KeyModifiers.Shift) ? '~' : '`',
            Key.OemBackslash => modifiers.HasFlag(KeyModifiers.Shift) ? '|' : '\\',
            Key.Oem8 => modifiers.HasFlag(KeyModifiers.Shift) ? '~' : '`',
            _ => -1
        };
    }

    private static int MapSpecial(Key key) => key switch
    {
        Key.Space => 0x20,
        Key.Return or Key.Enter => 0xff0d,
        Key.Back => 0xff08,
        Key.Tab => 0xff09,
        Key.Escape => 0xff1b,
        Key.Insert => 0xff63,
        Key.Delete => 0xffff,
        Key.Home => 0xff50,
        Key.End => 0xff57,
        Key.PageUp => 0xff55,
        Key.PageDown => 0xff56,
        Key.Left => 0xff51,
        Key.Up => 0xff52,
        Key.Right => 0xff53,
        Key.Down => 0xff54,
        Key.PrintScreen => 0xff61,
        Key.Scroll => 0xff14,
        Key.Pause => 0xff13,
        Key.NumLock => 0xff7f,
        Key.F1 => 0xffbe,
        Key.F2 => 0xffbf,
        Key.F3 => 0xffc0,
        Key.F4 => 0xffc1,
        Key.F5 => 0xffc2,
        Key.F6 => 0xffc3,
        Key.F7 => 0xffc4,
        Key.F8 => 0xffc5,
        Key.F9 => 0xffc6,
        Key.F10 => 0xffc7,
        Key.F11 => 0xffc8,
        Key.F12 => 0xffc9,
        Key.F13 => 0xffca,
        Key.F14 => 0xffcb,
        Key.F15 => 0xffcc,
        Key.F16 => 0xffcd,
        Key.F17 => 0xffce,
        Key.F18 => 0xffcf,
        Key.F19 => 0xffd0,
        Key.F20 => 0xffd1,
        Key.F21 => 0xffd2,
        Key.F22 => 0xffd3,
        Key.F23 => 0xffd4,
        Key.F24 => 0xffd5,
        Key.LeftShift => 0xffe1,
        Key.RightShift => 0xffe2,
        Key.LeftCtrl => 0xffe3,
        Key.RightCtrl => 0xffe4,
        Key.CapsLock => 0xffe5,
        Key.LeftAlt => 0xffe9,
        Key.RightAlt => 0xffea,
        Key.LWin => 0xffeb,
        Key.RWin => 0xffec,
        Key.Apps => 0xff67,
        Key.NumPad0 => 0xffb0,
        Key.NumPad1 => 0xffb1,
        Key.NumPad2 => 0xffb2,
        Key.NumPad3 => 0xffb3,
        Key.NumPad4 => 0xffb4,
        Key.NumPad5 => 0xffb5,
        Key.NumPad6 => 0xffb6,
        Key.NumPad7 => 0xffb7,
        Key.NumPad8 => 0xffb8,
        Key.NumPad9 => 0xffb9,
        Key.Divide => 0xffaf,
        Key.Multiply => 0xffaa,
        Key.Subtract => 0xffad,
        Key.Add => 0xffab,
        Key.Decimal => 0xffae,
        Key.Separator => 0xffac,
        _ => -1
    };
}
