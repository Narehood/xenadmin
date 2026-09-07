using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private const string DirectProxy = "Direct connection";
    private const string SystemProxy = "Use system proxy";
    private const string CustomProxy = "Use custom proxy";
    private const string BasicAuthentication = "Basic";
    private const string DigestAuthentication = "Digest";
    private readonly MainViewModel _main;
    private readonly Action _close;
    private readonly ShellAppSettings _settings;
    private bool _suppressPrivacyNotify;

    public SettingsViewModel(MainViewModel main, ShellAppSettings settings, Action close)
    {
        _main = main;
        _settings = settings;
        _close = close;
        _suppressPrivacyNotify = true;
        // Assign backing fields so On*Changed does not rewrite settings on open.
        _autoReconnectSavedServers = settings.AutoReconnectSavedServers;
        _rememberSavedServers = settings.RememberSavedServers;
        _autoRetryLostConnections = settings.AutoRetryLostConnections;
        _selectedProxyMode = settings.ProxyMode switch
        {
            ShellProxyMode.System => SystemProxy,
            ShellProxyMode.Custom => CustomProxy,
            _ => DirectProxy
        };
        _proxyAddress = settings.ProxyAddress;
        _proxyPortText = settings.ProxyPort.ToString();
        _bypassProxyForServers = settings.BypassProxyForServers;
        _provideProxyAuthentication = settings.ProvideProxyAuthentication;
        _proxyUsername = settings.GetProxyUsername();
        _proxyPassword = settings.GetProxyPassword();
        _selectedProxyAuthentication = settings.ProxyAuthentication == ShellProxyAuthentication.Basic
            ? BasicAuthentication
            : DigestAuthentication;
        _connectionTimeoutText = settings.ConnectionTimeoutSeconds.ToString();
        _warnUnrecognizedCertificates = settings.WarnUnrecognizedCertificates;
        _warnChangedCertificates = settings.WarnChangedCertificates;
        _warnPublicIpConnections = settings.WarnPublicIpConnections;
        _confirmAlertDismissals = settings.ConfirmAlertDismissals;
        _ignoreOvfValidationWarnings = settings.IgnoreOvfValidationWarnings;
        _fillPerformanceGraphAreas = settings.FillPerformanceGraphAreas;
        _scaleConsoleToFit = settings.ScaleConsoleToFit;
        _selectedConsoleReleaseShortcut = settings.ConsoleReleaseShortcut;
        _selectedConsoleFullscreenShortcut = settings.ConsoleFullscreenShortcut;
        _selectedConsoleDockShortcut = settings.ConsoleDockShortcut;
        _rememberLastSelectedTab = settings.RememberLastSelectedTab;
        _showTimestampsInLogs = settings.ShowTimestampsInLogs;
        _hideIpAddresses = settings.HideIpAddresses;
        _hideUuids = settings.HideUuids;
        _hideVmNames = settings.HideVmNames;
        _hideServerNames = settings.HideServerNames;
        _hideClusterNames = settings.HideClusterNames;
        _requireMainPassword = main.RequiresMainPassword;
        _suppressPrivacyNotify = false;
        VersionText = ShellVersionInfo.Display;
        BuildDateText = ShellVersionInfo.BuildDateDisplay;
        CodenameText = ShellVersionInfo.Codename;
        RefreshPasswordStorageNote();
        RefreshMainPasswordUi();
    }

    [ObservableProperty]
    private bool _autoReconnectSavedServers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAutoReconnectSavedServers))]
    private bool _rememberSavedServers;

    [ObservableProperty]
    private bool _autoRetryLostConnections;

    public IReadOnlyList<string> ProxyModes { get; } = [DirectProxy, SystemProxy, CustomProxy];

    public IReadOnlyList<string> ProxyAuthenticationMethods { get; } =
        [DigestAuthentication, BasicAuthentication];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCustomProxy))]
    [NotifyPropertyChangedFor(nameof(ShowProxyAuthentication))]
    private string _selectedProxyMode = DirectProxy;

    [ObservableProperty]
    private string _proxyAddress = string.Empty;

    [ObservableProperty]
    private string _proxyPortText = "80";

    [ObservableProperty]
    private bool _bypassProxyForServers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProxyAuthentication))]
    private bool _provideProxyAuthentication;

    [ObservableProperty]
    private string _proxyUsername = string.Empty;

    [ObservableProperty]
    private string _proxyPassword = string.Empty;

    [ObservableProperty]
    private string _selectedProxyAuthentication = DigestAuthentication;

    [ObservableProperty]
    private string _connectionTimeoutText = "20";

    [ObservableProperty]
    private bool _warnUnrecognizedCertificates;

    [ObservableProperty]
    private bool _warnChangedCertificates;

    [ObservableProperty]
    private bool _warnPublicIpConnections;

    [ObservableProperty]
    private bool _confirmAlertDismissals;

    [ObservableProperty]
    private bool _ignoreOvfValidationWarnings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecurityStatusMessage))]
    private string _securityStatusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProxyStatusMessage))]
    private string _proxyStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _fillPerformanceGraphAreas;

    [ObservableProperty]
    private bool _scaleConsoleToFit;

    public IReadOnlyList<string> ConsoleReleaseShortcuts { get; } = ["Right Ctrl", "Left Alt"];

    public IReadOnlyList<string> ConsoleFullscreenShortcuts { get; } =
        ["Ctrl+Enter", "Ctrl+Alt+F", "F12", "Ctrl+Alt"];

    public IReadOnlyList<string> ConsoleDockShortcuts { get; } = ["Alt+Shift+U", "F11", "None"];

    [ObservableProperty]
    private string _selectedConsoleReleaseShortcut = "Right Ctrl";

    [ObservableProperty]
    private string _selectedConsoleFullscreenShortcut = "Ctrl+Enter";

    [ObservableProperty]
    private string _selectedConsoleDockShortcut = "Alt+Shift+U";

    [ObservableProperty]
    private bool _rememberLastSelectedTab;

    [ObservableProperty]
    private bool _showTimestampsInLogs;

    [ObservableProperty]
    private bool _hideIpAddresses;

    [ObservableProperty]
    private bool _hideUuids;

    [ObservableProperty]
    private bool _hideVmNames;

    [ObservableProperty]
    private bool _hideServerNames;

    [ObservableProperty]
    private bool _hideClusterNames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeMainPassword))]
    [NotifyPropertyChangedFor(nameof(MainPasswordStatus))]
    private bool _requireMainPassword;

    [ObservableProperty]
    private string _versionText = string.Empty;

    [ObservableProperty]
    private string _buildDateText = string.Empty;

    [ObservableProperty]
    private string _codenameText = string.Empty;

    [ObservableProperty]
    private string _passwordStorageNote = string.Empty;

    [ObservableProperty]
    private string _mainPasswordStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateCheckStatus))]
    private string _updateCheckStatus = string.Empty;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    public bool HasUpdateCheckStatus => !string.IsNullOrEmpty(UpdateCheckStatus);

    public bool HasProxyStatusMessage => !string.IsNullOrEmpty(ProxyStatusMessage);

    public bool HasSecurityStatusMessage => !string.IsNullOrEmpty(SecurityStatusMessage);

    public bool ShowCustomProxy => string.Equals(SelectedProxyMode, CustomProxy, StringComparison.Ordinal);

    public bool ShowProxyAuthentication => ShowCustomProxy && ProvideProxyAuthentication;

    public bool CanChangeMainPassword => RequireMainPassword;

    public bool CanAutoReconnectSavedServers => RememberSavedServers;

    partial void OnAutoReconnectSavedServersChanged(bool value)
    {
        _settings.AutoReconnectSavedServers = value;
    }

    [RelayCommand]
    private async Task ToggleRememberSavedServersAsync()
    {
        if (_settings.RememberSavedServers)
        {
            var accepted = await ShellConfirmPrompt.ConfirmAsync(new ShellConfirmRequest
            {
                Title = "Stop remembering servers",
                Message = "Forget every saved server and its stored credentials? Active connections will stay open, but this list cannot be recovered.",
                AcceptLabel = "Forget saved servers",
                CancelLabel = "Keep saved servers"
            }).ConfigureAwait(true);
            if (!accepted)
            {
                OnPropertyChanged(nameof(RememberSavedServers));
                return;
            }

            _main.SetRememberSavedServers(false);
            RememberSavedServers = false;
            AutoReconnectSavedServers = false;
            return;
        }

        _main.SetRememberSavedServers(true);
        RememberSavedServers = true;
    }

    partial void OnAutoRetryLostConnectionsChanged(bool value)
    {
        _settings.AutoRetryLostConnections = value;
    }

    partial void OnWarnUnrecognizedCertificatesChanged(bool value)
    {
        _settings.WarnUnrecognizedCertificates = value;
    }

    partial void OnWarnChangedCertificatesChanged(bool value)
    {
        _settings.WarnChangedCertificates = value;
    }

    partial void OnWarnPublicIpConnectionsChanged(bool value)
    {
        _settings.WarnPublicIpConnections = value;
    }

    partial void OnConfirmAlertDismissalsChanged(bool value)
    {
        _settings.ConfirmAlertDismissals = value;
    }

    partial void OnIgnoreOvfValidationWarningsChanged(bool value)
    {
        _settings.IgnoreOvfValidationWarnings = value;
    }

    partial void OnFillPerformanceGraphAreasChanged(bool value)
    {
        _settings.FillPerformanceGraphAreas = value;
    }

    partial void OnScaleConsoleToFitChanged(bool value)
    {
        _settings.ScaleConsoleToFit = value;
    }

    partial void OnSelectedConsoleReleaseShortcutChanged(string value)
    {
        _settings.ConsoleReleaseShortcut = value;
    }

    partial void OnSelectedConsoleFullscreenShortcutChanged(string value)
    {
        _settings.ConsoleFullscreenShortcut = value;
    }

    partial void OnSelectedConsoleDockShortcutChanged(string value)
    {
        _settings.ConsoleDockShortcut = value;
    }

    partial void OnRememberLastSelectedTabChanged(bool value)
    {
        _settings.RememberLastSelectedTab = value;
        if (value)
            _main.PersistCurrentDetailTabPreference();
    }

    partial void OnShowTimestampsInLogsChanged(bool value)
    {
        _settings.ShowTimestampsInLogs = value;
    }

    [RelayCommand]
    private void SaveConnectionSettings()
    {
        if (!int.TryParse(ConnectionTimeoutText.Trim(), out var timeoutSeconds)
            || timeoutSeconds < 1
            || timeoutSeconds > 3600)
        {
            ProxyStatusMessage = "Enter a connection timeout between 1 and 3,600 seconds.";
            return;
        }

        var mode = SelectedProxyMode switch
        {
            SystemProxy => ShellProxyMode.System,
            CustomProxy => ShellProxyMode.Custom,
            _ => ShellProxyMode.Direct
        };

        var address = ProxyAddress.Trim();
        var port = 80;
        if (mode == ShellProxyMode.Custom)
        {
            if (Uri.CheckHostName(address) == UriHostNameType.Unknown)
            {
                ProxyStatusMessage = "Enter a valid proxy hostname or IP address without a URL path.";
                return;
            }

            if (!int.TryParse(ProxyPortText.Trim(), out port) || port is < 1 or > 65535)
            {
                ProxyStatusMessage = "Enter a proxy port between 1 and 65,535.";
                return;
            }

            if (ProvideProxyAuthentication && string.IsNullOrWhiteSpace(ProxyUsername))
            {
                ProxyStatusMessage = "Enter the proxy username.";
                return;
            }
        }

        _settings.ProxyMode = mode;
        _settings.ProxyAddress = address;
        _settings.ProxyPort = port;
        _settings.BypassProxyForServers = BypassProxyForServers;
        _settings.ProvideProxyAuthentication = mode == ShellProxyMode.Custom && ProvideProxyAuthentication;
        _settings.ProxyAuthentication = string.Equals(
            SelectedProxyAuthentication,
            BasicAuthentication,
            StringComparison.Ordinal)
            ? ShellProxyAuthentication.Basic
            : ShellProxyAuthentication.Digest;
        _settings.ConnectionTimeoutSeconds = timeoutSeconds;
        _settings.SetProxyCredentials(
            _settings.ProvideProxyAuthentication ? ProxyUsername : string.Empty,
            _settings.ProvideProxyAuthentication ? ProxyPassword : string.Empty);
        ShellBootstrap.ApplyProxySettings();

        ProxyStatusMessage = "Connection settings saved. Reconnect existing servers to apply proxy changes to their API sessions.";
    }

    partial void OnHideIpAddressesChanged(bool value)
    {
        if (_suppressPrivacyNotify)
            return;
        _settings.HideIpAddresses = value;
        _main.NotifyPrivacyChanged();
    }

    partial void OnHideUuidsChanged(bool value)
    {
        if (_suppressPrivacyNotify)
            return;
        _settings.HideUuids = value;
        _main.NotifyPrivacyChanged();
    }

    partial void OnHideVmNamesChanged(bool value)
    {
        if (_suppressPrivacyNotify)
            return;
        _settings.HideVmNames = value;
        _main.NotifyPrivacyChanged();
    }

    partial void OnHideServerNamesChanged(bool value)
    {
        if (_suppressPrivacyNotify)
            return;
        _settings.HideServerNames = value;
        _main.NotifyPrivacyChanged();
    }

    partial void OnHideClusterNamesChanged(bool value)
    {
        if (_suppressPrivacyNotify)
            return;
        _settings.HideClusterNames = value;
        _main.NotifyPrivacyChanged();
    }

    [RelayCommand]
    private async Task ToggleRequireMainPasswordAsync()
    {
        string? next = null;
        if (_main.RequiresMainPassword)
        {
            if (!await _main.PromptEnterMainPasswordAsync().ConfigureAwait(true))
            {
                OnPropertyChanged(nameof(RequireMainPassword));
                return;
            }
        }
        else
        {
            next = await _main.PromptSetMainPasswordAsync().ConfigureAwait(true);
            if (next == null)
            {
                OnPropertyChanged(nameof(RequireMainPassword));
                return;
            }
        }
        var changed = await _main.ChangeSavedPasswordProtectionAsync(next).ConfigureAwait(true);
        _suppressPrivacyNotify = true;
        RequireMainPassword = _main.RequiresMainPassword;
        _suppressPrivacyNotify = false;
        UpdateCheckStatus = changed
            ? next == null ? "Main password disabled." : "Main password enabled. Saved credentials are protected."
            : _main.StatusMessage;
        RefreshMainPasswordUi();
        RefreshPasswordStorageNote();
    }

    [RelayCommand]
    private async Task ChangeMainPasswordAsync()
    {
        if (!_main.RequiresMainPassword)
            return;
        var next = await _main.PromptChangeMainPasswordAsync().ConfigureAwait(true);
        if (next == null)
            return;
        var changed = await _main.ChangeSavedPasswordProtectionAsync(next).ConfigureAwait(true);
        RefreshMainPasswordUi();
        UpdateCheckStatus = changed ? "Main password changed." : _main.StatusMessage;
    }

    private void RefreshMainPasswordUi()
    {
        MainPasswordStatus = RequireMainPassword
            ? "A main password is required at the start of each session to unlock saved credentials."
            : "When set, the main password protects all saved server login credentials.";
        OnPropertyChanged(nameof(CanChangeMainPassword));
    }

    private void RefreshPasswordStorageNote()
    {
        if (_main.RequiresMainPassword)
        {
            PasswordStorageNote =
                "Saved passwords use authenticated encryption with a key derived from your main password. Enter it when the app starts to reconnect.";
            return;
        }

        PasswordStorageNote = OperatingSystem.IsWindows()
            ? "Passwords are protected with Windows DPAPI for your user account."
            : "Passwords are encrypted with a per-user key file under ~/.config/XCP-ng/XCP-ng Center Shell/.";
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingUpdates)
            return;

        IsCheckingUpdates = true;
        UpdateCheckStatus = "Checking GitHub Releases…";
        try
        {
            var message = await _main.CheckForUpdatesManualAsync().ConfigureAwait(true);
            UpdateCheckStatus = message;
        }
        catch (Exception ex)
        {
            UpdateCheckStatus = $"Update check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    private void ClearTrustedCertificates()
    {
        _main.ClearTrustedCertificatesCommand.Execute(null);
        SecurityStatusMessage = _main.StatusMessage;
    }

    [RelayCommand]
    private void Close() => _close();
}
