using System.Reflection;
using System.Text.RegularExpressions;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Client version helpers. Assembly versions use <c>year.month.day.revision</c>.
/// </summary>
public static class ShellVersionInfo
{
    private static readonly Regex VersionToken = new(
        @"\d+(?:\.\d+){1,3}",
        RegexOptions.Compiled);

    public static string Display
    {
        get
        {
            var version = Current;
            return version.Major == 0 && version.Minor == 0 && version.Build == 0 && version.Revision <= 0
                ? "Preview"
                : version.ToString(4);
        }
    }

    public static Version Current
    {
        get
        {
            var asm = typeof(ShellVersionInfo).Assembly;
            var info = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .OfType<AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var cleaned = info;
                var plus = cleaned.IndexOf('+');
                if (plus > 0)
                    cleaned = cleaned[..plus];
                if (TryParse(cleaned, out var fromInfo))
                    return fromInfo;
            }

            return asm.GetName().Version ?? new Version(0, 0, 0, 0);
        }
    }

    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().TrimStart('v', 'V');
        var match = VersionToken.Match(trimmed);
        if (!match.Success)
            return false;

        try
        {
            version = new Version(NormalizeFourPart(match.Value));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeFourPart(string value)
    {
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out _) ? p : "0")
            .ToList();
        while (parts.Count < 4)
            parts.Add("0");
        return string.Join('.', parts.Take(4));
    }
}
