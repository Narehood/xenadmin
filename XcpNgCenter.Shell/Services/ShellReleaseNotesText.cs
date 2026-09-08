using System.Text.RegularExpressions;

namespace XcpNgCenter.Shell.Services;

internal static class ShellReleaseNotesText
{
    // Render release text without remote HTML, images, or executable content.
    internal static string ToPlainText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "No patch notes were provided for this release.";
        var text = Regex.Replace(markdown, @"!?\[([^\]]*)\]\([^\s)]+\)", "$1");
        text = Regex.Replace(text, @"(?m)^\s{0,3}#{1,6}\s+", "");
        text = Regex.Replace(text, @"(?m)^\s*[-*]\s+", "• ");
        return text.Replace("**", "").Replace("`", "").Trim();
    }
}
