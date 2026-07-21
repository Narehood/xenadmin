namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Shared config directory for shell prefs / TOFU / saved servers.
/// Uses ApplicationData (Windows AppData, Linux ~/.config via XDG).
/// </summary>
public static class ShellPaths
{
    public const string ProductFolderName = "XCP-ng Center Shell";

    public static string GetConfigRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdg))
                appData = xdg;
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(home))
                    home = Environment.GetEnvironmentVariable("HOME") ?? ".";
                appData = Path.Combine(home, ".config");
            }
        }

        var root = Path.Combine(appData, "XCP-ng", ProductFolderName);
        Directory.CreateDirectory(root);
        return root;
    }
}
