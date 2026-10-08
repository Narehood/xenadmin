using System.Text;

namespace XcpNgCenter.Shell.Services;

internal static class ShellResourceText
{
    /// <summary>Converts WinForms resource mnemonics to plain text while preserving escaped ampersands.</summary>
    public static string WithoutMnemonics(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (!text.Contains('&')) return text;

        var result = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '&') result.Append(text[index]);
            else if (index + 1 < text.Length && text[index + 1] == '&')
            {
                result.Append('&');
                index++;
            }
        }
        return result.ToString();
    }
}
