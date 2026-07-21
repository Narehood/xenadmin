namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Shared config directory for shell prefs / TOFU / saved servers.
/// Windows: %APPDATA%\XCP-ng\… · Linux/macOS: $XDG_CONFIG_HOME or ~/.config/XCP-ng/…
/// </summary>
public static class ShellPaths
{
    public const string ProductFolderName = "XCP-ng Center Shell";

    public static string GetConfigRoot()
    {
        var root = Path.Combine(GetPlatformConfigHome(), "XCP-ng", ProductFolderName);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string GetPlatformConfigHome()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
                return appData;
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        // Prefer XDG config explicitly — do not rely on ApplicationData, which can be
        // empty or diverge from docs on some Linux sessions.
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            return xdg!;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = ".";

        return Path.Combine(home, ".config");
    }
}
