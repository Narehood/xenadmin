using System.Text.Json;
using System.Text.Json.Serialization;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// App-level preferences for the Avalonia shell (auto-reconnect, etc.).
/// </summary>
public sealed class ShellAppSettings
{
    private readonly string _path;
    private readonly object _gate = new();
    private State _state;

    public ShellAppSettings(string? path = null)
    {
        var root = GetConfigRoot();
        Directory.CreateDirectory(root);
        _path = path ?? Path.Combine(root, "app-settings.json");
        _state = Load();
    }

    public bool AutoReconnectSavedServers
    {
        get
        {
            lock (_gate)
                return _state.AutoReconnectSavedServers;
        }
        set
        {
            lock (_gate)
            {
                if (_state.AutoReconnectSavedServers == value)
                    return;
                _state.AutoReconnectSavedServers = value;
                SaveUnlocked();
            }
        }
    }

    private State Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new State();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<State>(json) ?? new State();
        }
        catch
        {
            return new State();
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Best-effort.
        }
    }

    private static string GetConfigRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "XCP-ng", "XCP-ng Center Shell");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(xdg))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                home = Environment.GetEnvironmentVariable("HOME") ?? ".";
            xdg = Path.Combine(home, ".config");
        }

        return Path.Combine(xdg!, "XCP-ng", "XCP-ng Center Shell");
    }

    private sealed class State
    {
        [JsonPropertyName("autoReconnectSavedServers")]
        public bool AutoReconnectSavedServers { get; set; } = true;
    }
}
