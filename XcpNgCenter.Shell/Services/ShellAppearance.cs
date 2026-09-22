using Avalonia.Media;

namespace XcpNgCenter.Shell.Services;

public enum ShellThemeMode
{
    Dark,
    Light,
    System
}

/// <summary>Shell palette generation, including readable custom-accent text.</summary>
public static class ShellAppearance
{
    public const string DefaultAccent = "#F07318";

    public static string NormalizeAccent(string? value)
    {
        // Persist only opaque RGB hex, independent of culture or named-color parsing.
        if (value is not { Length: 7 } || value[0] != '#'
            || !value[1..].All(Uri.IsHexDigit))
            return DefaultAccent;
        return value.ToUpperInvariant();
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static double Luminance(Color color)
    {
        static double Linear(byte component)
        {
            var value = component / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    public static Color ForegroundOn(Color background) =>
        Luminance(background) > 0.179 ? Colors.Black : Colors.White;

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    public static Color ReadableColor(Color color, Color background)
    {
        var target = ForegroundOn(background);
        var backgroundLuminance = Luminance(background);
        for (var step = 0; step <= 100; step++)
        {
            var candidate = Blend(color, target, step / 100d);
            var luminance = Luminance(candidate);
            var contrast = (Math.Max(luminance, backgroundLuminance) + 0.05)
                / (Math.Min(luminance, backgroundLuminance) + 0.05);
            if (contrast >= 4.5)
                return candidate;
        }
        return target;
    }

    public static IReadOnlyDictionary<string, Color> CreatePalette(bool light, string accentHex)
    {
        var accent = Color.Parse(NormalizeAccent(accentHex));
        var deep = Color.Parse(light ? "#F3F5F7" : "#0E1216");
        var panel = Color.Parse(light ? "#FFFFFF" : "#161C22");
        var elevated = Color.Parse(light ? "#E8EDF2" : "#1E262E");
        // Keep the same readable label on normal/hover/pressed accent buttons.
        var foreground = ForegroundOn(accent);
        var hover = Blend(accent, foreground == Colors.Black ? Colors.White : Colors.Black, 0.14);
        return new Dictionary<string, Color>
        {
            ["BgDeep"] = deep,
            ["BgPanel"] = panel,
            ["BgElevated"] = elevated,
            ["BgHover"] = Color.Parse(light ? "#DCE3EA" : "#273139"),
            ["StrokeSubtle"] = Color.Parse(light ? "#C2CCD6" : "#2E3943"),
            ["TextPrimary"] = Color.Parse(light ? "#17212B" : "#F2F4F6"),
            ["TextMuted"] = Color.Parse(light ? "#526170" : "#9AA6B2"),
            ["BrandOrange"] = accent,
            ["BrandOrangeDeep"] = hover,
            ["AccentForeground"] = foreground,
            ["AccentText"] = ReadableColor(accent, elevated),
            ["AccentSubtle"] = Color.FromArgb(20, accent.R, accent.G, accent.B),
            ["BrandGlow"] = Color.FromArgb(64, accent.R, accent.G, accent.B),
            ["AccentTransparent"] = Color.FromArgb(0, accent.R, accent.G, accent.B),
            ["BgGradientMid"] = Blend(deep, panel, 0.5),
            ["BgGradientEnd"] = Blend(deep, accent, 0.06),
            ["Danger"] = Color.Parse(light ? "#B42332" : "#E35D5D"),
            ["Ok"] = Color.Parse(light ? "#147D46" : "#3DBE7A")
        };
    }
}
