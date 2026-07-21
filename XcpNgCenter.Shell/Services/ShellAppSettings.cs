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
        // Defer directory creation to SaveUnlocked().
        _path = path ?? Path.Combine(ShellPaths.GetConfigRoot(ensureExists: false), "app-settings.json");
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
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Best-effort.
        }
    }

    private sealed class State
    {
        [JsonPropertyName("autoReconnectSavedServers")]
        public bool AutoReconnectSavedServers { get; set; } = true;
    }
}
