using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ShellUpdateChannelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shell-channel-" + Guid.NewGuid().ToString("N"));
    private ShellUpdatePreferences Preferences => new(Path.Combine(_root, "settings", "updates.json"));
    private static Version NewVersion(int revision = 1) => new(ShellVersionInfo.Current.Major + 1, 1, 1, revision);

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"updateChannel\":99}")]
    [InlineData("{\"updateChannel\":\"beta\"}")]
    [InlineData("{\"dismissedUpdateVersion\":\"2026.1.1.1\"}")]
    public void ExistingOrInvalidPreferencesDefaultToRegularReleases(string? json)
    {
        if (json != null)
        {
            Directory.CreateDirectory(Path.Combine(_root, "settings"));
            File.WriteAllText(Path.Combine(_root, "settings", "updates.json"), json);
        }
        Assert.Equal(ShellUpdateChannel.Stable, Preferences.GetChannel());
        if (json == null) Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void ChannelAndIndependentDismissalsSurviveRestart()
    {
        Preferences.SetDismissedVersion("2026.1.1.1");
        Preferences.SetChannel(ShellUpdateChannel.Beta);
        Assert.Equal(ShellUpdateChannel.Beta, Preferences.GetChannel());
        Assert.Null(Preferences.GetDismissedVersion());
        Preferences.SetDismissedVersion("2026.1.1.2");
        Preferences.SetChannel(ShellUpdateChannel.Stable);
        Assert.Equal("2026.1.1.1", Preferences.GetDismissedVersion());
        Preferences.ClearDismissedVersion();
        Preferences.SetChannel(ShellUpdateChannel.Beta);
        Assert.Equal("2026.1.1.2", Preferences.GetDismissedVersion());
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "settings")));
    }

    [Fact]
    public async Task BetaFindsHighestCanonicalVersionAcrossPagesAndLabelsNotes()
    {
        Preferences.SetChannel(ShellUpdateChannel.Beta);
        using var handler = new Handler(path =>
        {
            Assert.DoesNotContain("/latest", path);
            return path.EndsWith("page=1")
                ? JsonSerializer.Serialize(Enumerable.Repeat(Release(ShellVersionInfo.Current), 99)
                    .Append(Release(NewVersion(1))))
                : JsonSerializer.Serialize(new[] { Release(NewVersion(3), beta: true), Release(NewVersion(2), beta: true),
                    Release(NewVersion(9), draft: true), Release(NewVersion(8), tag: "v" + NewVersion(8) + "-beta") });
        });
        using var http = new HttpClient(handler);
        var result = await new ShellGitHubUpdateChecker(Preferences, httpClient: http).CheckForUpdateDetailedAsync();
        Assert.Equal(ShellUpdateCheckStatus.Available, result.Status);
        Assert.Equal(NewVersion(3), result.Offer!.Version);
        Assert.True(result.Offer.IsPrerelease);
        Assert.EndsWith(" (beta)", result.Offer.DisplayVersion);
        Assert.Equal(new[] { NewVersion(3), NewVersion(2), NewVersion(1) }, result.Offer.ReleaseNotes.Select(n => n.Version));
        Assert.EndsWith(" (beta)", result.Offer.ReleaseNotes[0].Heading);
        Assert.False(result.Offer.ReleaseNotes[2].IsPrerelease);
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegularReleaseCanSupersedeBetaWithoutLeakingBetaIntoRegularChannel(bool beta)
    {
        Preferences.SetChannel(beta ? ShellUpdateChannel.Beta : ShellUpdateChannel.Stable);
        using var handler = new Handler(path => path.EndsWith("/latest")
            ? JsonSerializer.Serialize(Release(NewVersion(3)))
            : JsonSerializer.Serialize(new[] { Release(NewVersion(2), beta: true), Release(NewVersion(3)) }));
        using var http = new HttpClient(handler);
        var result = await new ShellGitHubUpdateChecker(Preferences, httpClient: http).CheckForUpdateDetailedAsync();
        Assert.Equal(NewVersion(3), result.Offer!.Version);
        Assert.False(result.Offer.IsPrerelease);
        Assert.Equal(beta ? 2 : 1, result.Offer.ReleaseNotes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChannelNeverOffersADowngrade(bool beta)
    {
        Preferences.SetChannel(beta ? ShellUpdateChannel.Beta : ShellUpdateChannel.Stable);
        using var handler = new Handler(path => path.EndsWith("/latest")
            ? JsonSerializer.Serialize(Release(new Version(2000, 1, 1, 1)))
            : JsonSerializer.Serialize(new[] { Release(new Version(2000, 1, 1, 1), beta: true) }));
        using var http = new HttpClient(handler);
        Assert.Equal(ShellUpdateCheckStatus.UpToDate,
            (await new ShellGitHubUpdateChecker(Preferences, httpClient: http).CheckForUpdateDetailedAsync()).Status);
    }

    [Fact]
    public async Task DismissalInRegularChannelDoesNotHideBetaOffer()
    {
        Preferences.SetDismissedVersion(NewVersion().ToString(4));
        Preferences.SetChannel(ShellUpdateChannel.Beta);
        using var handler = new Handler(_ => JsonSerializer.Serialize(new[] { Release(NewVersion(), beta: true) }));
        using var http = new HttpClient(handler);
        var checker = new ShellGitHubUpdateChecker(Preferences, httpClient: http);
        Assert.Equal(ShellUpdateCheckStatus.Available, (await checker.CheckForUpdateDetailedAsync()).Status);
        checker.Dismiss(NewVersion());
        Assert.Equal(ShellUpdateCheckStatus.Dismissed, (await checker.CheckForUpdateDetailedAsync()).Status);
        Assert.Equal(ShellUpdateCheckStatus.Available, (await checker.CheckForUpdateDetailedAsync(ignoreDismissed: true)).Status);
    }

    [Fact]
    public async Task BetaCatalogFailureIsNotReportedAsUpToDate()
    {
        Preferences.SetChannel(ShellUpdateChannel.Beta);
        using var handler = new Handler(_ => throw new HttpRequestException("offline"));
        using var http = new HttpClient(handler);
        var result = await new ShellGitHubUpdateChecker(Preferences, httpClient: http).CheckForUpdateDetailedAsync();
        Assert.Equal(ShellUpdateCheckStatus.Failed, result.Status);
        Assert.Contains("offline", result.Detail);
    }

    [Theory]
    [InlineData(false, false, false, false, true)]
    [InlineData(false, true, false, false, true)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    public async Task FreshPublisherAuthenticationRequiresExplicitBetaOptIn(
        bool beta, bool allowBeta, bool draft, bool wrongTag, bool accepted)
    {
        using var handler = new Handler(path =>
        {
            Assert.EndsWith("/releases/tags/v" + NewVersion(), path);
            return JsonSerializer.Serialize(Release(NewVersion(), beta, draft, wrongTag ? "v1.1.1.1" : null));
        });
        using var http = new HttpClient(handler);
        var authenticate = ShellGitHubUpdateChecker.AuthenticatePublishedAssetAsync(NewVersion(), "Narehood", "xenadmin",
            http, allowBeta, CancellationToken.None);
        if (accepted) Assert.StartsWith("sha256:", (await authenticate).Digest);
        else await Assert.ThrowsAsync<InvalidDataException>(() => authenticate);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BootstrapCarriesExplicitBetaChoiceAcrossElevation(bool beta, bool elevate)
    {
        var install = Path.Combine(_root, "install");
        var executable = OperatingSystem.IsWindows() ? "XcpNgCenter.Shell.exe" : "XcpNgCenter.Shell";
        var info = ShellUpdateInstaller.CreateBootstrapStartInfo(Path.Combine(install, executable), install,
            Path.Combine(_root, "cache"), Path.Combine(_root, "launch"), NewVersion(), 123, elevate, beta);
        Assert.Equal(beta, info.ArgumentList.Contains("--allow-beta-release"));
        Assert.Equal(elevate, info.UseShellExecute);
        Assert.DoesNotContain(info.ArgumentList, a => a.Contains("sha256:") || a.Contains("https://"));
    }

    [Fact]
    public void ChangingChannelInvalidatesPriorOfferAndNotes()
    {
        var main = IsolatedMain();
        SetField(main, "_pendingUpdate", new ShellUpdateOffer(NewVersion(), "v" + NewVersion(), "Beta", "", null, null));
        main.UpdateAvailable = true;
        main.UpdateReleaseNotes = [new ShellReleaseNotes(NewVersion(), "beta notes", "")];
        Assert.True(main.TrySetBetaUpdates(true, out _));
        Assert.True(main.UseBetaUpdates);
        Assert.False(main.UpdateAvailable);
        Assert.False(main.ShowUpdateAction);
        Assert.Empty(main.UpdateReleaseNotes);
        Assert.False(main.DownloadUpdateCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedArchiveRetainsReleaseKindAndRejectsMismatchedOffer(bool beta)
    {
        var install = Path.Combine(_root, "install");
        var installer = new ShellUpdateInstaller(install, Path.Combine(install, "XcpNgCenter.Shell.exe"),
            true, false, System.Runtime.InteropServices.Architecture.X64, Path.Combine(_root, "stage"));
        var cache = (string)typeof(ShellUpdateInstaller).GetMethod("GetUpdateRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(installer, [NewVersion()])!;
        Directory.CreateDirectory(cache);
        var asset = new ShellUpdateAsset($"XcpNgCenter.Shell-win-x64-{NewVersion()}.zip", "", 3, "sha256:" + new string('a', 64));
        File.WriteAllBytes(Path.Combine(cache, asset.Name), [1, 2, 3]);
        File.WriteAllText(Path.Combine(cache, "update.json"), JsonSerializer.Serialize(new
        {
            version = NewVersion().ToString(4), tagName = "v" + NewVersion(), prerelease = beta,
            installDirectory = install, executableName = "XcpNgCenter.Shell.exe", assetName = asset.Name, digest = asset.Digest
        }));
        var offer = new ShellUpdateOffer(NewVersion(), "v" + NewVersion(), "", "", null, asset) { IsPrerelease = beta };
        Assert.Equal(beta, installer.TryGetPreparedUpdate(offer)!.IsPrerelease);
        Assert.Null(installer.TryGetPreparedUpdate(offer with { IsPrerelease = !beta }));
        Assert.Null(installer.TryGetPreparedUpdate(offer with { TagName = "other" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChannelCannotChangeDuringCheckOrInstallation(bool downloading)
    {
        var main = IsolatedMain();
        if (downloading) main.IsUpdateDownloading = true;
        else main.IsUpdateChecking = true;
        Assert.False(main.CanChangeUpdateChannel);
        Assert.False(main.TrySetBetaUpdates(true, out _));
        Assert.False(main.UseBetaUpdates);
    }

    private MainViewModel IsolatedMain()
    {
        // Exercise production update state without starting profile loading or server connections.
        var main = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        SetField(main, "_updateChecker", new ShellGitHubUpdateChecker(Preferences));
        return main;
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object Release(Version version, bool beta = false, bool draft = false, string? tag = null)
    {
        var name = OperatingSystem.IsWindows() ? $"XcpNgCenter.Shell-win-x64-{version}.zip"
            : $"XcpNgCenter.Shell-linux-x64-{version}.tar.gz";
        return new { tag_name = tag ?? "v" + version, prerelease = beta, draft, body = "Release notes",
            assets = new[] { new { name, state = "uploaded", size = 100,
                browser_download_url = $"https://github.com/Narehood/xenadmin/releases/download/v{version}/{name}",
                digest = "sha256:" + new string('a', 64) } } };
    }

    private sealed class Handler(Func<string, string> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(respond(request.RequestUri!.PathAndQuery)) });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
