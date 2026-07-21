using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class MainViewModel
{
    private readonly ShellGitHubUpdateChecker _updateChecker = new();
    private CancellationTokenSource? _updateCheckCts;
    private ShellUpdateOffer? _pendingUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateBanner))]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _updateBannerTitle = string.Empty;

    [ObservableProperty]
    private string _updateBannerMessage = string.Empty;

    public bool ShowUpdateBanner => UpdateAvailable && !string.IsNullOrWhiteSpace(UpdateBannerTitle);

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
    }

    private void ApplyUpdateOffer(ShellUpdateOffer offer)
    {
        _pendingUpdate = offer;
        UpdateBannerTitle = $"Update available — {offer.Version.ToString(4)}";
        UpdateBannerMessage = string.IsNullOrWhiteSpace(offer.Title) || offer.Title == offer.TagName
            ? $"A newer build than {ShellVersionInfo.Display} is on GitHub Releases."
            : offer.Title;
        UpdateAvailable = true;
    }

    [RelayCommand]
    private void OpenUpdateRelease()
    {
        var url = _pendingUpdate?.HtmlUrl ?? _updateChecker.ReleasesPageUrl;
        if (!ShellExternalOpener.TryOpenUrl(url, out var error))
            StatusMessage = $"Could not open release page: {error}";
    }

    [RelayCommand]
    private void DismissUpdateBanner()
    {
        if (_pendingUpdate != null)
            _updateChecker.Dismiss(_pendingUpdate.Version);

        UpdateAvailable = false;
        UpdateBannerTitle = string.Empty;
        UpdateBannerMessage = string.Empty;
        _pendingUpdate = null;
    }
}
