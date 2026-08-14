using System.Text.Json;
using System.Text.Json.Serialization;

namespace XcpNgCenter.Shell.Services;

public enum ShellProxyMode
{
    Direct,
    System,
    Custom
}

public enum ShellProxyAuthentication
{
    Basic,
    Digest
}

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

    public bool RememberSavedServers
    {
        get
        {
            lock (_gate)
                return _state.RememberSavedServers;
        }
        set => SetBool(v => _state.RememberSavedServers = v, () => _state.RememberSavedServers, value);
    }

    /// <summary>
    /// When true, lost connections stay registered so XenConnection may retry.
    /// When false (default), a lost connection stays in the tree as disconnected until the user reconnects.
    /// </summary>
    public bool AutoRetryLostConnections
    {
        get
        {
            lock (_gate)
                return _state.AutoRetryLostConnections;
        }
        set => SetBool(v => _state.AutoRetryLostConnections = v, () => _state.AutoRetryLostConnections, value);
    }

    public ShellProxyMode ProxyMode
    {
        get
        {
            lock (_gate)
                return Enum.IsDefined(typeof(ShellProxyMode), _state.ProxyMode)
                    ? (ShellProxyMode)_state.ProxyMode
                    : ShellProxyMode.Direct;
        }
        set => SetInt(v => _state.ProxyMode = v, () => _state.ProxyMode, (int)value);
    }

    public string ProxyAddress
    {
        get
        {
            lock (_gate)
                return _state.ProxyAddress;
        }
        set => SetString(v => _state.ProxyAddress = v, () => _state.ProxyAddress, value);
    }

    public int ProxyPort
    {
        get
        {
            lock (_gate)
                return _state.ProxyPort;
        }
        set => SetInt(v => _state.ProxyPort = v, () => _state.ProxyPort, value);
    }

    public bool BypassProxyForServers
    {
        get
        {
            lock (_gate)
                return _state.BypassProxyForServers;
        }
        set => SetBool(v => _state.BypassProxyForServers = v, () => _state.BypassProxyForServers, value);
    }

    public bool ProvideProxyAuthentication
    {
        get
        {
            lock (_gate)
                return _state.ProvideProxyAuthentication;
        }
        set => SetBool(
            v => _state.ProvideProxyAuthentication = v,
            () => _state.ProvideProxyAuthentication,
            value);
    }

    public ShellProxyAuthentication ProxyAuthentication
    {
        get
        {
            lock (_gate)
                return Enum.IsDefined(typeof(ShellProxyAuthentication), _state.ProxyAuthentication)
                    ? (ShellProxyAuthentication)_state.ProxyAuthentication
                    : ShellProxyAuthentication.Digest;
        }
        set => SetInt(
            v => _state.ProxyAuthentication = v,
            () => _state.ProxyAuthentication,
            (int)value);
    }

    public int ConnectionTimeoutSeconds
    {
        get
        {
            lock (_gate)
                return _state.ConnectionTimeoutSeconds;
        }
        set => SetInt(
            v => _state.ConnectionTimeoutSeconds = v,
            () => _state.ConnectionTimeoutSeconds,
            value);
    }

    public bool WarnUnrecognizedCertificates
    {
        get
        {
            lock (_gate)
                return _state.WarnUnrecognizedCertificates;
        }
        set => SetBool(
            v => _state.WarnUnrecognizedCertificates = v,
            () => _state.WarnUnrecognizedCertificates,
            value);
    }

    public bool WarnChangedCertificates
    {
        get
        {
            lock (_gate)
                return _state.WarnChangedCertificates;
        }
        set => SetBool(
            v => _state.WarnChangedCertificates = v,
            () => _state.WarnChangedCertificates,
            value);
    }

    public bool WarnPublicIpConnections
    {
        get
        {
            lock (_gate)
                return _state.WarnPublicIpConnections;
        }
        set => SetBool(
            v => _state.WarnPublicIpConnections = v,
            () => _state.WarnPublicIpConnections,
            value);
    }

    public bool ConfirmAlertDismissals
    {
        get
        {
            lock (_gate)
                return _state.ConfirmAlertDismissals;
        }
        set => SetBool(
            v => _state.ConfirmAlertDismissals = v,
            () => _state.ConfirmAlertDismissals,
            value);
    }

    public bool IgnoreOvfValidationWarnings
    {
        get
        {
            lock (_gate)
                return _state.IgnoreOvfValidationWarnings;
        }
        set => SetBool(
            v => _state.IgnoreOvfValidationWarnings = v,
            () => _state.IgnoreOvfValidationWarnings,
            value);
    }

    public string GetProxyUsername()
    {
        string? encrypted;
        lock (_gate)
            encrypted = _state.ProxyUsername;
        return SavedServerStore.UnprotectPassword(encrypted) ?? string.Empty;
    }

    public string GetProxyPassword()
    {
        string? encrypted;
        lock (_gate)
            encrypted = _state.ProxyPassword;
        return SavedServerStore.UnprotectPassword(encrypted) ?? string.Empty;
    }

    public void SetProxyCredentials(string username, string password)
    {
        var protectedUsername = SavedServerStore.ProtectPassword(username);
        var protectedPassword = SavedServerStore.ProtectPassword(password);
        lock (_gate)
        {
            if (string.Equals(_state.ProxyUsername, protectedUsername, StringComparison.Ordinal)
                && string.Equals(_state.ProxyPassword, protectedPassword, StringComparison.Ordinal))
            {
                return;
            }

            _state.ProxyUsername = protectedUsername;
            _state.ProxyPassword = protectedPassword;
            SaveUnlocked();
        }

        Changed?.Invoke();
    }

    public bool FillPerformanceGraphAreas
    {
        get
        {
            lock (_gate)
                return _state.FillPerformanceGraphAreas;
        }
        set => SetBool(v => _state.FillPerformanceGraphAreas = v, () => _state.FillPerformanceGraphAreas, value);
    }

    public bool ScaleConsoleToFit
    {
        get
        {
            lock (_gate)
                return _state.ScaleConsoleToFit;
        }
        set => SetBool(v => _state.ScaleConsoleToFit = v, () => _state.ScaleConsoleToFit, value);
    }

    public string ConsoleReleaseShortcut
    {
        get
        {
            lock (_gate)
                return _state.ConsoleReleaseShortcut;
        }
        set => SetString(
            v => _state.ConsoleReleaseShortcut = v,
            () => _state.ConsoleReleaseShortcut,
            value);
    }

    public string ConsoleFullscreenShortcut
    {
        get
        {
            lock (_gate)
                return _state.ConsoleFullscreenShortcut;
        }
        set => SetString(
            v => _state.ConsoleFullscreenShortcut = v,
            () => _state.ConsoleFullscreenShortcut,
            value);
    }

    public string ConsoleDockShortcut
    {
        get
        {
            lock (_gate)
                return _state.ConsoleDockShortcut;
        }
        set => SetString(
            v => _state.ConsoleDockShortcut = v,
            () => _state.ConsoleDockShortcut,
            value);
    }

    public bool RememberLastSelectedTab
    {
        get
        {
            lock (_gate)
                return _state.RememberLastSelectedTab;
        }
        set => SetBool(v => _state.RememberLastSelectedTab = v, () => _state.RememberLastSelectedTab, value);
    }

    public bool ShowTimestampsInLogs
    {
        get
        {
            lock (_gate)
                return _state.ShowTimestampsInLogs;
        }
        set => SetBool(v => _state.ShowTimestampsInLogs = v, () => _state.ShowTimestampsInLogs, value);
    }

    public int LastSelectedDetailTab
    {
        get
        {
            lock (_gate)
                return _state.LastSelectedDetailTab;
        }
        set => SetInt(v => _state.LastSelectedDetailTab = v, () => _state.LastSelectedDetailTab, value);
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

    private void SetInt(Action<int> assign, Func<int> current, int value)
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

    private void SetString(Action<string> assign, Func<string> current, string? value)
    {
        value ??= string.Empty;
        lock (_gate)
        {
            if (string.Equals(current(), value, StringComparison.Ordinal))
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

        [JsonPropertyName("rememberSavedServers")]
        public bool RememberSavedServers { get; set; } = true;

        [JsonPropertyName("autoRetryLostConnections")]
        public bool AutoRetryLostConnections { get; set; }

        [JsonPropertyName("proxyMode")]
        public int ProxyMode { get; set; }

        [JsonPropertyName("proxyAddress")]
        public string ProxyAddress { get; set; } = string.Empty;

        [JsonPropertyName("proxyPort")]
        public int ProxyPort { get; set; } = 80;

        [JsonPropertyName("bypassProxyForServers")]
        public bool BypassProxyForServers { get; set; }

        [JsonPropertyName("provideProxyAuthentication")]
        public bool ProvideProxyAuthentication { get; set; }

        [JsonPropertyName("proxyAuthentication")]
        public int ProxyAuthentication { get; set; } = (int)ShellProxyAuthentication.Digest;

        [JsonPropertyName("proxyUsername")]
        public string? ProxyUsername { get; set; }

        [JsonPropertyName("proxyPassword")]
        public string? ProxyPassword { get; set; }

        [JsonPropertyName("connectionTimeoutSeconds")]
        public int ConnectionTimeoutSeconds { get; set; } = 20;

        [JsonPropertyName("warnUnrecognizedCertificates")]
        public bool WarnUnrecognizedCertificates { get; set; } = true;

        [JsonPropertyName("warnChangedCertificates")]
        public bool WarnChangedCertificates { get; set; } = true;

        [JsonPropertyName("warnPublicIpConnections")]
        public bool WarnPublicIpConnections { get; set; } = true;

        [JsonPropertyName("confirmAlertDismissals")]
        public bool ConfirmAlertDismissals { get; set; } = true;

        [JsonPropertyName("ignoreOvfValidationWarnings")]
        public bool IgnoreOvfValidationWarnings { get; set; }

        [JsonPropertyName("fillPerformanceGraphAreas")]
        public bool FillPerformanceGraphAreas { get; set; }

        [JsonPropertyName("scaleConsoleToFit")]
        public bool ScaleConsoleToFit { get; set; } = true;

        [JsonPropertyName("consoleReleaseShortcut")]
        public string ConsoleReleaseShortcut { get; set; } = "Right Ctrl";

        [JsonPropertyName("consoleFullscreenShortcut")]
        public string ConsoleFullscreenShortcut { get; set; } = "Ctrl+Enter";

        [JsonPropertyName("consoleDockShortcut")]
        public string ConsoleDockShortcut { get; set; } = "Alt+Shift+U";

        [JsonPropertyName("rememberLastSelectedTab")]
        public bool RememberLastSelectedTab { get; set; } = true;

        [JsonPropertyName("showTimestampsInLogs")]
        public bool ShowTimestampsInLogs { get; set; } = true;

        [JsonPropertyName("lastSelectedDetailTab")]
        public int LastSelectedDetailTab { get; set; }

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
