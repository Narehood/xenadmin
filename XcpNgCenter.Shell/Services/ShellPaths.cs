namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Shared config directory for shell prefs / TOFU / saved servers.
/// Windows: %APPDATA%\XCP-ng\… · Linux/macOS: $XDG_CONFIG_HOME or ~/.config/XCP-ng/…
/// </summary>
public static class ShellPaths
{
    public const string ProductFolderName = "XCP-ng Center Shell";

    /// <param name="ensureExists">
    /// When true (default), creates the directory. Prefer false from constructors that
    /// should not touch the filesystem until a save/key path runs.
    /// </param>
    public static string GetConfigRoot(bool ensureExists = true)
    {
        var root = Path.Combine(GetPlatformConfigHome(), "XCP-ng", ProductFolderName);
        if (ensureExists)
            Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Non-roaming storage for downloaded update packages.
    /// Windows: %LOCALAPPDATA%; Linux/macOS: $XDG_CACHE_HOME or ~/.cache.
    /// </summary>
    public static string GetUpdateStagingRoot(bool ensureExists = true)
    {
        var root = Path.Combine(GetPlatformLocalDataHome(), "XCP-ng", ProductFolderName, "Updates");
        if (ensureExists)
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

    private static string GetPlatformLocalDataHome()
    {
        if (OperatingSystem.IsWindows())
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localData))
                return localData;
            return GetPlatformConfigHome();
        }

        var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgCache))
            return xdgCache!;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = ".";

        return Path.Combine(home, ".cache");
    }
}
