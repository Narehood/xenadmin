using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class MainViewModel
{
    private readonly ShellGitHubUpdateChecker _updateChecker = new();
    private readonly ShellUpdateInstaller _updateInstaller = new();
    private CancellationTokenSource? _updateCheckCts;
    private CancellationTokenSource? _updateDownloadCts;
    private ShellUpdateOffer? _pendingUpdate;
    private PreparedShellUpdate? _preparedUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateBusy))]
    [NotifyPropertyChangedFor(nameof(IsUpdateProgressIndeterminate))]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    private bool _isUpdateChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateIndicator))]
    private bool _hasUpdateError;

    [ObservableProperty]
    private IReadOnlyList<ShellReleaseNotes> _updateReleaseNotes = [];

    [ObservableProperty]
    private string _updateReleaseNotesMessage = string.Empty;

    public bool IsUpdateBusy => IsUpdateChecking || IsUpdateDownloading;
    public bool IsUpdateProgressIndeterminate => IsUpdateChecking
        || (IsUpdateDownloading && (UpdateDownloadProgress <= 0 || UpdateDownloadProgress >= 100));
    public double UpdateProgressSweepAngle => Math.Clamp(UpdateDownloadProgress, 0, 100) * 3.6;
    public bool ShowUpdateIndicator => UpdateAvailable || HasUpdateError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateBanner))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateIndicator))]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _updateBannerTitle = "Check for updates";

    [ObservableProperty]
    private string _updateBannerMessage = "Click the update button to check GitHub for a newer build.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateProgress))]
    [NotifyPropertyChangedFor(nameof(CanDismissUpdate))]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    [NotifyPropertyChangedFor(nameof(IsUpdateBusy))]
    [NotifyPropertyChangedFor(nameof(IsUpdateProgressIndeterminate))]
    private bool _isUpdateDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateProgressSweepAngle))]
    [NotifyPropertyChangedFor(nameof(IsUpdateProgressIndeterminate))]
    private double _updateDownloadProgress;

    [ObservableProperty]
    private string _updateProgressText = string.Empty;

    [ObservableProperty]
    private string _updateActionLabel = "Download update";

    public bool ShowUpdateBanner => UpdateAvailable && !string.IsNullOrWhiteSpace(UpdateBannerTitle);

    public bool ShowUpdateProgress => IsUpdateDownloading;

    public bool CanDismissUpdate => !IsUpdateDownloading;

    public bool ShowUpdateAction =>
        _pendingUpdate != null
        && (_preparedUpdate != null
            || (_pendingUpdate.Asset != null && _updateInstaller.CanInstallInPlace(out _)));

    private void InitializeUpdateCheck()
    {
        _updateCheckCts = new CancellationTokenSource();
        var token = _updateCheckCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2500, token).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => CheckForUpdatesManualAsync(token));
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private void DisposeUpdateCheck()
    {
        try
        {
            _updateCheckCts?.Cancel();
            _updateCheckCts?.Dispose();
        }
        catch
        {
            // Best-effort.
        }

        _updateCheckCts = null;

        try
        {
            _updateDownloadCts?.Cancel();
            _updateDownloadCts?.Dispose();
        }
        catch
        {
            // Best-effort.
        }

        _updateDownloadCts = null;
    }

    private void ApplyUpdateOffer(ShellUpdateOffer offer)
    {
        _pendingUpdate = offer;
        HasUpdateError = false;
        UpdateReleaseNotes = offer.ReleaseNotes;
        UpdateReleaseNotesMessage = offer.ReleaseNotesError ?? string.Empty;
        _preparedUpdate = _updateInstaller.TryGetPreparedUpdate(offer);
        UpdateDownloadProgress = 0;
        UpdateProgressText = string.Empty;

        var releaseMessage = string.IsNullOrWhiteSpace(offer.Title) || offer.Title == offer.TagName
            ? $"A newer build than {ShellVersionInfo.Display} is on GitHub Releases."
            : offer.Title;

        if (_preparedUpdate != null)
        {
            var failure = _updateInstaller.TryGetApplyFailure(offer);
            if (string.IsNullOrWhiteSpace(failure) && ShellUpdateInstaller.StartupUpdateFailed)
                failure = ShellUpdateInstaller.StartupStatusMessage;

            if (!string.IsNullOrWhiteSpace(failure))
            {
                HasUpdateError = true;
                UpdateBannerTitle = $"Update install failed — {offer.Version.ToString(4)}";
                UpdateBannerMessage = failure;
                UpdateActionLabel = "Retry install";
                UpdateDownloadProgress = 100;
                UpdateProgressText = "Download verified; install did not complete.";
            }
            else
            {
                ApplyPreparedUpdateState(offer);
            }
        }
        else
        {
            UpdateBannerTitle = $"Update available — {offer.Version.ToString(4)}";
            UpdateActionLabel = "Download update";
            if (offer.Asset == null)
            {
                UpdateBannerMessage = $"{releaseMessage} No compatible automatic-install package is attached; use View release.";
            }
            else if (!_updateInstaller.CanInstallInPlace(out var reason))
            {
                UpdateBannerMessage = $"{releaseMessage} {reason}";
            }
            else
            {
                var permissionMessage = _updateInstaller.RequiresElevationForInstall
                    ? " Windows will request administrator approval to replace files in this Program Files (or other protected) installation."
                    : string.Empty;
                UpdateBannerMessage = $"{releaseMessage} Download and verify it here, then restart to install it.{permissionMessage}";
            }
        }

        UpdateAvailable = true;
        OnPropertyChanged(nameof(ShowUpdateAction));
        DownloadUpdateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OpenUpdateRelease()
    {
        var url = _pendingUpdate?.HtmlUrl ?? _updateChecker.ReleasesPageUrl;
        if (!ShellExternalOpener.TryOpenUrl(url, out var error))
            StatusMessage = $"Could not open release page: {error}";
    }

    private bool CanDownloadUpdate() => ShowUpdateAction && !IsUpdateBusy;

    [RelayCommand(CanExecute = nameof(CanDownloadUpdate))]
    private async Task DownloadUpdateAsync()
    {
        if (_pendingUpdate == null)
            return;

        if (_preparedUpdate != null)
        {
            _updateInstaller.ClearApplyFailure(_preparedUpdate);
            await PromptToRestartForUpdateAsync().ConfigureAwait(true);
            return;
        }

        if (_pendingUpdate.Asset == null)
            return;

        _updateDownloadCts?.Dispose();
        _updateDownloadCts = new CancellationTokenSource();
        var token = _updateDownloadCts.Token;
        PreparedShellUpdate? prepared = null;

        try
        {
            UpdateDownloadProgress = 0;
            IsUpdateDownloading = true;
            UpdateActionLabel = "Downloading…";
            UpdateBannerTitle = $"Downloading {_pendingUpdate.Version.ToString(4)}";
            HasUpdateError = false;
            UpdateBannerMessage = "Downloading and verifying the update. You can continue using XCP-ng Center.";
            var progress = new Progress<ShellUpdateProgress>(update =>
            {
                UpdateDownloadProgress = update.Percentage;
                UpdateProgressText = FormatUpdateProgress(update);
            });
            prepared = await _updateInstaller.PrepareAsync(_pendingUpdate, progress, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            UpdateBannerTitle = $"Update available — {_pendingUpdate.Version.ToString(4)}";
            UpdateBannerMessage = "The update download was cancelled.";
            UpdateActionLabel = "Retry download";
        }
        catch (Exception ex)
        {
            HasUpdateError = true;
            UpdateBannerTitle = $"Could not download {_pendingUpdate.Version.ToString(4)}";
            UpdateBannerMessage = ex.Message;
            UpdateActionLabel = "Retry download";
            StatusMessage = $"Update download failed: {ex.Message}";
        }
        finally
        {
            IsUpdateDownloading = false;
            _updateDownloadCts?.Dispose();
            _updateDownloadCts = null;
        }

        if (prepared == null)
            return;

        _preparedUpdate = prepared;
        ApplyPreparedUpdateState(_pendingUpdate);
        OnPropertyChanged(nameof(ShowUpdateAction));
        DownloadUpdateCommand.NotifyCanExecuteChanged();
        // The user chooses when to restart from the update control. Do not open
        // a confirmation dialog as a side effect of completing a download.
    }

    private void ApplyPreparedUpdateState(ShellUpdateOffer offer)
    {
        UpdateBannerTitle = $"Update ready — {offer.Version.ToString(4)}";
        UpdateBannerMessage = _updateInstaller.RequiresElevationForInstall
            ? "The update is downloaded. Installation will verify it again and requires Windows administrator approval."
            : "The update is downloaded. Restart to verify and install it in the current application directory.";
        UpdateDownloadProgress = 100;
        UpdateProgressText = "Download ready.";
        UpdateActionLabel = "Restart & install";
    }

    private async Task PromptToRestartForUpdateAsync()
    {
        if (_pendingUpdate == null || _preparedUpdate == null)
            return;

        var requiresElevation = _updateInstaller.RequiresElevationForInstall;
        var permissionMessage = requiresElevation
            ? " Windows will show an administrator approval prompt before replacing the protected application files. Only the installer helper is elevated; XCP-ng Center will reopen normally."
            : string.Empty;
        var restart = await ShellConfirmPrompt.ConfirmAsync(new ShellConfirmRequest
        {
            Title = $"Restart to install {_pendingUpdate.Version.ToString(4)}?",
            Message = $"XCP-ng Center will verify the downloaded update, then close, replace the files in {_updateInstaller.InstallDirectory}, and reopen automatically. Active server sessions will be closed.{permissionMessage}",
            AcceptLabel = requiresElevation ? "Continue to approval" : "Restart & install",
            CancelLabel = "Later"
        }).ConfigureAwait(true);
        if (!restart)
            return;

        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                throw new InvalidOperationException("The desktop application lifetime is unavailable.");

            IsUpdateDownloading = true;
            UpdateBannerTitle = "Preparing to install the update";
            UpdateBannerMessage = "Verifying the package and starting the update progress window. This may take a few minutes.";
            UpdateActionLabel = "Preparing installation…";
            UpdateProgressText = "Verifying the package for installation…";
            await _updateInstaller.StartApplyHelperAsync(_preparedUpdate).ConfigureAwait(true);
            StatusMessage = "Restarting to install the update…";
            desktop.Shutdown(0);
        }
        catch (OperationCanceledException ex)
        {
            HasUpdateError = true;
            UpdateBannerTitle = "Administrator approval cancelled";
            UpdateBannerMessage = ex.Message;
            UpdateActionLabel = "Retry install";
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            HasUpdateError = true;
            UpdateBannerTitle = "Could not start the update";
            UpdateBannerMessage = ex.Message;
            UpdateActionLabel = "Retry restart";
            StatusMessage = $"Could not start the update: {ex.Message}";
        }
        finally
        {
            IsUpdateDownloading = false;
        }
    }

    private static string FormatUpdateProgress(ShellUpdateProgress progress)
    {
        if (progress.TotalBytes <= 0)
            return $"{progress.Status} {progress.BytesReceived / 1024d / 1024d:N1} MB";

        return $"{progress.Status} {progress.BytesReceived / 1024d / 1024d:N1} of {progress.TotalBytes / 1024d / 1024d:N1} MB ({progress.Percentage:N0}%)";
    }

    /// <summary>Manual update check used by Settings → About.</summary>
    public async Task<string> CheckForUpdatesManualAsync(CancellationToken cancellationToken = default)
    {
        if (IsUpdateBusy) return "An update operation is already in progress.";
        try
        {
            IsUpdateChecking = true;
            HasUpdateError = false;
            UpdateBannerTitle = "Checking for updates…";
            // Never treat a dismissed update or a network failure as "up to date".
            var result = await _updateChecker
                .CheckForUpdateDetailedAsync(ignoreDismissed: true, cancellationToken)
                .ConfigureAwait(true);

            switch (result.Status)
            {
                case ShellUpdateCheckStatus.Available when result.Offer != null:
                    _updateChecker.ClearDismissed();
                    ApplyUpdateOffer(result.Offer);
                    return $"Update available: {result.Offer.Version.ToString(4)} — use the update button to review and download it.";

                case ShellUpdateCheckStatus.Dismissed when result.Offer != null:
                    // ignoreDismissed:true should not return Dismissed; keep a safe fallback.
                    _updateChecker.ClearDismissed();
                    ApplyUpdateOffer(result.Offer);
                    return $"Update available: {result.Offer.Version.ToString(4)} — use the update button to review and download it.";

                case ShellUpdateCheckStatus.Failed:
                    HasUpdateError = true;
                    UpdateBannerTitle = "Could not check for updates";
                    UpdateBannerMessage = result.Detail ?? "Click to retry the update check.";
                    return $"Update check failed: {result.Detail ?? "Unknown error."}";

                case ShellUpdateCheckStatus.UpToDate:
                default:
                    UpdateAvailable = false;
                    _pendingUpdate = null;
                    _preparedUpdate = null;
                    UpdateReleaseNotes = [];
                    UpdateReleaseNotesMessage = string.Empty;
                    UpdateBannerTitle = "You're up to date";
                    UpdateBannerMessage = result.Detail ?? $"Current build: {ShellVersionInfo.Display}.";
                    OnPropertyChanged(nameof(ShowUpdateAction));
                    DownloadUpdateCommand.NotifyCanExecuteChanged();
                    return result.Detail ?? $"You're up to date ({ShellVersionInfo.Display}).";
            }
        }
        catch (Exception ex)
        {
            HasUpdateError = true;
            UpdateBannerTitle = "Could not check for updates";
            UpdateBannerMessage = ex.Message;
            return $"Update check failed: {ex.Message}";
        }
        finally { IsUpdateChecking = false; }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync() =>
        StatusMessage = await CheckForUpdatesManualAsync(_updateCheckCts?.Token ?? CancellationToken.None);

    [RelayCommand]
    private void OpenReleaseNotes(ShellReleaseNotes? notes)
    {
        if (notes != null && !ShellExternalOpener.TryOpenUrl(notes.HtmlUrl, out var error))
            StatusMessage = $"Could not open release notes: {error}";
    }

    [RelayCommand]
    private void DismissUpdateBanner()
    {
        if (IsUpdateDownloading)
            return;

        if (_pendingUpdate != null)
            _updateChecker.Dismiss(_pendingUpdate.Version);
        if (_preparedUpdate != null)
            _updateInstaller.DiscardPreparedUpdate(_preparedUpdate);

        UpdateAvailable = false;
        UpdateBannerTitle = string.Empty;
        UpdateBannerMessage = string.Empty;
        UpdateProgressText = string.Empty;
        _pendingUpdate = null;
        _preparedUpdate = null;
        OnPropertyChanged(nameof(ShowUpdateAction));
        DownloadUpdateCommand.NotifyCanExecuteChanged();
    }
}
