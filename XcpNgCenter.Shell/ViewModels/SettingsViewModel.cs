using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
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
        _autoRetryLostConnections = settings.AutoRetryLostConnections;
        _hideIpAddresses = settings.HideIpAddresses;
        _hideUuids = settings.HideUuids;
        _hideVmNames = settings.HideVmNames;
        _hideServerNames = settings.HideServerNames;
        _hideClusterNames = settings.HideClusterNames;
        _requireMainPassword = settings.RequireMainPassword && settings.GetMainPasswordHash() != null;
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
    private bool _autoRetryLostConnections;

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

    public bool CanChangeMainPassword => RequireMainPassword && _settings.GetMainPasswordHash() != null;

    partial void OnAutoReconnectSavedServersChanged(bool value)
    {
        _settings.AutoReconnectSavedServers = value;
    }

    partial void OnAutoRetryLostConnectionsChanged(bool value)
    {
        _settings.AutoRetryLostConnections = value;
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
        var currentlyEnabled = _settings.RequireMainPassword && _settings.GetMainPasswordHash() != null;
        if (currentlyEnabled)
        {
            var hash = _settings.GetMainPasswordHash();
            if (hash == null)
            {
                await DisableMainPasswordAsync().ConfigureAwait(true);
                return;
            }

            var ok = await _main.PromptEnterMainPasswordAsync(hash).ConfigureAwait(true);
            if (!ok)
            {
                // Checkbox is OneWay; force UI to re-sync after cancel.
                OnPropertyChanged(nameof(RequireMainPassword));
                return;
            }

            await DisableMainPasswordAsync().ConfigureAwait(true);
        }
        else
        {
            var set = await _main.PromptSetMainPasswordAsync().ConfigureAwait(true);
            if (set == null)
            {
                OnPropertyChanged(nameof(RequireMainPassword));
                return;
            }

            _main.SetSessionMainPassword(set.Value.Hash, set.Value.Plain);
            await EnableMainPasswordAsync(set.Value.Hash).ConfigureAwait(true);
        }

        RefreshMainPasswordUi();
        RefreshPasswordStorageNote();
    }

    [RelayCommand]
    private async Task ChangeMainPasswordAsync()
    {
        var current = _settings.GetMainPasswordHash();
        if (current == null)
            return;

        var changed = await _main.PromptChangeMainPasswordAsync(current).ConfigureAwait(true);
        if (changed == null)
            return;

        await _main.ReencryptSavedPasswordsForMainPasswordChangeAsync(
            changed.Value.CurrentPlain,
            changed.Value.Hash).ConfigureAwait(true);
        _settings.SetMainPasswordHash(changed.Value.Hash);
        _main.SetSessionMainPassword(changed.Value.Hash, changed.Value.NewPlain);
        RefreshMainPasswordUi();
        UpdateCheckStatus = "Main password changed.";
    }

    private async Task EnableMainPasswordAsync(byte[] hash)
    {
        await _main.MigrateSavedPasswordsToMainPasswordAsync(hash).ConfigureAwait(true);
        _settings.SetMainPasswordHash(hash);
        _settings.RequireMainPassword = true;
        _main.SetSessionMainPassword(hash);
        _suppressPrivacyNotify = true;
        RequireMainPassword = true;
        _suppressPrivacyNotify = false;
        UpdateCheckStatus = "Main password enabled. Saved credentials are protected.";
    }

    private async Task DisableMainPasswordAsync()
    {
        await _main.MigrateSavedPasswordsFromMainPasswordAsync().ConfigureAwait(true);
        _settings.RequireMainPassword = false;
        _settings.SetMainPasswordHash(null);
        _main.ClearSessionMainPassword();
        _suppressPrivacyNotify = true;
        RequireMainPassword = false;
        _suppressPrivacyNotify = false;
        UpdateCheckStatus = "Main password disabled.";
    }

    private void RefreshMainPasswordUi()
    {
        MainPasswordStatus = RequireMainPassword && _settings.GetMainPasswordHash() != null
            ? "A main password is required at the start of each session to unlock saved credentials."
            : "When set, the main password protects all saved server login credentials.";
        OnPropertyChanged(nameof(CanChangeMainPassword));
    }

    private void RefreshPasswordStorageNote()
    {
        if (_settings.RequireMainPassword && _settings.GetMainPasswordHash() != null)
        {
            PasswordStorageNote =
                "Saved passwords are encrypted with your main password (AES). Enter it when the app starts to reconnect.";
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
        UpdateCheckStatus = _main.StatusMessage;
    }

    [RelayCommand]
    private void Close() => _close();
}
