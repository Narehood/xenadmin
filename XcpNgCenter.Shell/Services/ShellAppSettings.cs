using System.Text.Json;
using System.Text.Json.Serialization;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// App-level preferences for the Avalonia shell (auto-reconnect, privacy, main password).
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

    public event Action? Changed;

    public bool AutoReconnectSavedServers
    {
        get
        {
            lock (_gate)
                return _state.AutoReconnectSavedServers;
        }
        set => SetBool(v => _state.AutoReconnectSavedServers = v, () => _state.AutoReconnectSavedServers, value);
    }

    public bool HideIpAddresses
    {
        get
        {
            lock (_gate)
                return _state.HideIpAddresses;
        }
        set => SetBool(v => _state.HideIpAddresses = v, () => _state.HideIpAddresses, value);
    }

    public bool HideUuids
    {
        get
        {
            lock (_gate)
                return _state.HideUuids;
        }
        set => SetBool(v => _state.HideUuids = v, () => _state.HideUuids, value);
    }

    public bool HideVmNames
    {
        get
        {
            lock (_gate)
                return _state.HideVmNames;
        }
        set => SetBool(v => _state.HideVmNames = v, () => _state.HideVmNames, value);
    }

    public bool HideServerNames
    {
        get
        {
            lock (_gate)
                return _state.HideServerNames;
        }
        set => SetBool(v => _state.HideServerNames = v, () => _state.HideServerNames, value);
    }

    public bool HideClusterNames
    {
        get
        {
            lock (_gate)
                return _state.HideClusterNames;
        }
        set => SetBool(v => _state.HideClusterNames = v, () => _state.HideClusterNames, value);
    }

    public bool RequireMainPassword
    {
        get
        {
            lock (_gate)
                return _state.RequireMainPassword;
        }
        set => SetBool(v => _state.RequireMainPassword = v, () => _state.RequireMainPassword, value);
    }

    /// <summary>Base64-encoded SHA-256 hash of the main password, or null when unset.</summary>
    public string? MainPasswordHashBase64
    {
        get
        {
            lock (_gate)
                return _state.MainPasswordHashBase64;
        }
        set
        {
            lock (_gate)
            {
                if (_state.MainPasswordHashBase64 == value)
                    return;
                _state.MainPasswordHashBase64 = value;
                SaveUnlocked();
            }

            Changed?.Invoke();
        }
    }

    public byte[]? GetMainPasswordHash()
    {
        var b64 = MainPasswordHashBase64;
        if (string.IsNullOrWhiteSpace(b64))
            return null;
        try
        {
            return Convert.FromBase64String(b64);
        }
        catch
        {
            return null;
        }
    }

    public void SetMainPasswordHash(byte[]? hash)
    {
        MainPasswordHashBase64 = hash == null || hash.Length == 0
            ? null
            : Convert.ToBase64String(hash);
    }

    private void SetBool(Action<bool> assign, Func<bool> current, bool value)
    {
        lock (_gate)
        {
            if (current() == value)
                return;
            assign(value);
            SaveUnlocked();
        }

        Changed?.Invoke();
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

        [JsonPropertyName("hideIpAddresses")]
        public bool HideIpAddresses { get; set; }

        [JsonPropertyName("hideUuids")]
        public bool HideUuids { get; set; }

        [JsonPropertyName("hideVmNames")]
        public bool HideVmNames { get; set; }

        [JsonPropertyName("hideServerNames")]
        public bool HideServerNames { get; set; }

        [JsonPropertyName("hideClusterNames")]
        public bool HideClusterNames { get; set; }

        [JsonPropertyName("requireMainPassword")]
        public bool RequireMainPassword { get; set; }

        [JsonPropertyName("mainPasswordHash")]
        public string? MainPasswordHashBase64 { get; set; }
    }
}
