namespace XcpNgCenter.Shell.Services;

/// <summary>Validation happens for the entire draft before any key is sent.</summary>
public static class ConsolePasteText
{
    public const int MaxLength = 4096;

    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string? GetError(string? text, bool allowEnterAndTab)
    {
        if (string.IsNullOrEmpty(text))
            return "Load or enter some text first.";
        if (text.Length > MaxLength)
            return $"Paste is limited to {MaxLength:N0} characters. Split the text into smaller parts.";
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t')
                continue;
            if (ch < ' ' || ch > '~')
                return $"Unsupported character U+{(int)ch:X4}. Use printable ASCII text; hidden controls and non-ASCII characters cannot be pasted.";
        }
        if (!allowEnterAndTab && text.Any(ch => ch is '\r' or '\n' or '\t'))
            return "This text includes Enter or Tab keys. Review it and explicitly allow these keys before sending.";
        return null;
    }

    public static int KeySym(char ch) => ch switch
    {
        '\n' => 0xff0d,
        '\t' => 0xff09,
        >= ' ' and <= '~' => ch,
        _ => throw new ArgumentException("Unsupported paste character.")
    };
}
