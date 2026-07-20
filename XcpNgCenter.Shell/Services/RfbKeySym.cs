using Avalonia.Input;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Maps Avalonia keys to RFB/X11 keysyms for hosted console input.
/// Covers the common Latin + navigation set; full WinForms KeyMap remains the long-term reference.
/// </summary>
public static class RfbKeySym
{
    public static int FromKey(Key key, KeyModifiers modifiers)
    {
        // Prefer unicode for printable Latin when Shift is the only modifier.
        if (key is >= Key.A and <= Key.Z)
        {
            var baseChar = (char)('a' + (key - Key.A));
            var shifted = modifiers.HasFlag(KeyModifiers.Shift);
            return shifted ? char.ToUpperInvariant(baseChar) : baseChar;
        }

        if (key is >= Key.D0 and <= Key.D9 && !modifiers.HasFlag(KeyModifiers.Shift))
            return '0' + (key - Key.D0);

        return key switch
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
            Key.LeftShift or Key.RightShift => 0xffe1,
            Key.LeftCtrl or Key.RightCtrl => 0xffe3,
            Key.LeftAlt => 0xffe9,
            Key.RightAlt => 0xffea,
            Key.LWin or Key.RWin => 0xffeb,
            Key.CapsLock => 0xffe5,
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
            // US shifted digits
            Key.D1 when modifiers.HasFlag(KeyModifiers.Shift) => '!',
            Key.D2 when modifiers.HasFlag(KeyModifiers.Shift) => '@',
            Key.D3 when modifiers.HasFlag(KeyModifiers.Shift) => '#',
            Key.D4 when modifiers.HasFlag(KeyModifiers.Shift) => '$',
            Key.D5 when modifiers.HasFlag(KeyModifiers.Shift) => '%',
            Key.D6 when modifiers.HasFlag(KeyModifiers.Shift) => '^',
            Key.D7 when modifiers.HasFlag(KeyModifiers.Shift) => '&',
            Key.D8 when modifiers.HasFlag(KeyModifiers.Shift) => '*',
            Key.D9 when modifiers.HasFlag(KeyModifiers.Shift) => '(',
            Key.D0 when modifiers.HasFlag(KeyModifiers.Shift) => ')',
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
            _ => -1
        };
    }
}
