using Avalonia.Platform.Storage;

namespace XcpNgCenter.Shell.Views;

internal static class ShellFilePicker
{
    /// <summary>GTK/portal-friendly "all files" pattern (avoid "*.*" which skips extensionless files).</summary>
    public static FilePickerFileType AllFiles { get; } = new("All files") { Patterns = ["*"] };

    public static string? LocalPathOrNull(IStorageFile? file)
    {
        if (file == null)
            return null;
        var path = file.TryGetLocalPath();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public static string? LocalPathOrNull(IStorageFolder? folder)
    {
        if (folder == null)
            return null;
        var path = folder.TryGetLocalPath();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}
