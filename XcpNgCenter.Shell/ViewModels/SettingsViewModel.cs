using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly Action _close;
    private readonly ShellAppSettings _settings;

    public SettingsViewModel(MainViewModel main, ShellAppSettings settings, Action close)
    {
        _main = main;
        _settings = settings;
        _close = close;
        // Assign backing fields so On*Changed does not rewrite settings on open.
        _autoReconnectSavedServers = settings.AutoReconnectSavedServers;
        _autoRetryLostConnections = settings.AutoRetryLostConnections;
        VersionText = ShellVersionInfo.Display;
        BuildDateText = ShellVersionInfo.BuildDateDisplay;
        CodenameText = ShellVersionInfo.Codename;
        PasswordStorageNote = OperatingSystem.IsWindows()
            ? "Passwords are protected with Windows DPAPI for your user account."
            : "Passwords are encrypted with a per-user key file under ~/.config/XCP-ng/XCP-ng Center Shell/.";
    }

    [ObservableProperty]
    private bool _autoReconnectSavedServers;

    [ObservableProperty]
    private bool _autoRetryLostConnections;

    [ObservableProperty]
    private string _versionText = string.Empty;

    [ObservableProperty]
    private string _buildDateText = string.Empty;

    [ObservableProperty]
    private string _codenameText = string.Empty;

    [ObservableProperty]
    private string _passwordStorageNote = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateCheckStatus))]
    private string _updateCheckStatus = string.Empty;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    public bool HasUpdateCheckStatus => !string.IsNullOrEmpty(UpdateCheckStatus);

    partial void OnAutoReconnectSavedServersChanged(bool value)
    {
        _settings.AutoReconnectSavedServers = value;
    }

    partial void OnAutoRetryLostConnectionsChanged(bool value)
    {
        _settings.AutoRetryLostConnections = value;
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
