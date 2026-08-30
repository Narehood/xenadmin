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
    [NotifyPropertyChangedFor(nameof(ShowUpdateBanner))]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _updateBannerTitle = string.Empty;

    [ObservableProperty]
    private string _updateBannerMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateProgress))]
    [NotifyPropertyChangedFor(nameof(CanDismissUpdate))]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    private bool _isUpdateDownloading;

    [ObservableProperty]
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
            // Let the main window paint first.
            await Task.Delay(2500, token).ConfigureAwait(false);
            var offer = await _updateChecker.CheckForUpdateAsync(token).ConfigureAwait(false);
            if (offer == null || token.IsCancellationRequested)
                return;

            Dispatcher.UIThread.Post(() => ApplyUpdateOffer(offer));
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
        _preparedUpdate = _updateInstaller.TryGetPreparedUpdate(offer);
        UpdateDownloadProgress = 0;
        UpdateProgressText = string.Empty;

        var releaseMessage = string.IsNullOrWhiteSpace(offer.Title) || offer.Title == offer.TagName
            ? $"A newer build than {ShellVersionInfo.Display} is on GitHub Releases."
            : offer.Title;

        if (_preparedUpdate != null)
        {
            ApplyPreparedUpdateState(offer);
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

    private bool CanDownloadUpdate() => ShowUpdateAction && !IsUpdateDownloading;

    [RelayCommand(CanExecute = nameof(CanDownloadUpdate))]
    private async Task DownloadUpdateAsync()
    {
        if (_pendingUpdate == null)
            return;

        if (_preparedUpdate != null)
        {
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
            IsUpdateDownloading = true;
            UpdateActionLabel = "Downloading…";
            UpdateBannerTitle = $"Downloading {_pendingUpdate.Version.ToString(4)}";
            UpdateBannerMessage = $"Staging the release asset under {_updateInstaller.StagingDirectory}.";
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
        await PromptToRestartForUpdateAsync().ConfigureAwait(true);
    }

    private void ApplyPreparedUpdateState(ShellUpdateOffer offer)
    {
        UpdateBannerTitle = $"Update ready — {offer.Version.ToString(4)}";
        UpdateBannerMessage = _updateInstaller.RequiresElevationForInstall
            ? "The update was downloaded and verified. Windows administrator approval is required to replace files in this protected installation folder."
            : "The update was downloaded and verified. Restart to install it in the current application directory.";
        UpdateDownloadProgress = 100;
        UpdateProgressText = "Download verified.";
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
            Message = $"The update is downloaded and verified. XCP-ng Center will close, replace the files in {_updateInstaller.InstallDirectory}, and reopen automatically. Active server sessions will be closed.{permissionMessage}",
            AcceptLabel = requiresElevation ? "Continue to approval" : "Restart & install",
            CancelLabel = "Later"
        }).ConfigureAwait(true);
        if (!restart)
            return;

        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                throw new InvalidOperationException("The desktop application lifetime is unavailable.");

            _updateInstaller.StartApplyHelper(_preparedUpdate);
            StatusMessage = "Restarting to install the update…";
            desktop.Shutdown(0);
        }
        catch (OperationCanceledException ex)
        {
            UpdateBannerTitle = "Administrator approval cancelled";
            UpdateBannerMessage = ex.Message;
            UpdateActionLabel = "Retry install";
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            UpdateBannerTitle = "Could not start the update";
            UpdateBannerMessage = ex.Message;
            UpdateActionLabel = "Retry restart";
            StatusMessage = $"Could not start the update: {ex.Message}";
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
        try
        {
            // Never treat a dismissed update or a network failure as "up to date".
            var result = await _updateChecker
                .CheckForUpdateDetailedAsync(ignoreDismissed: true, cancellationToken)
                .ConfigureAwait(true);

            switch (result.Status)
            {
                case ShellUpdateCheckStatus.Available when result.Offer != null:
                    _updateChecker.ClearDismissed();
                    ApplyUpdateOffer(result.Offer);
                    return $"Update available: {result.Offer.Version.ToString(4)} — use the banner to download it or open the release page.";

                case ShellUpdateCheckStatus.Dismissed when result.Offer != null:
                    // ignoreDismissed:true should not return Dismissed; keep a safe fallback.
                    _updateChecker.ClearDismissed();
                    ApplyUpdateOffer(result.Offer);
                    return $"Update available: {result.Offer.Version.ToString(4)} — use the banner to download it or open the release page.";

                case ShellUpdateCheckStatus.Failed:
                    return $"Update check failed: {result.Detail ?? "Unknown error."}";

                case ShellUpdateCheckStatus.UpToDate:
                default:
                    UpdateAvailable = false;
                    _pendingUpdate = null;
                    _preparedUpdate = null;
                    OnPropertyChanged(nameof(ShowUpdateAction));
                    DownloadUpdateCommand.NotifyCanExecuteChanged();
                    return result.Detail ?? $"You're up to date ({ShellVersionInfo.Display}).";
            }
        }
        catch (Exception ex)
        {
            return $"Update check failed: {ex.Message}";
        }
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
