using System.Net;
using System.Text.Json;
using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ShellReleaseHistoryTests
{
    [Fact]
    public async Task CollectsAllNewerStableVersionsAcrossPagesInVersionOrder()
    {
        var current = ShellVersionInfo.Current;
        var middle = new Version(current.Major + 1, 1, 1, 1);
        var latest = new Version(current.Major + 1, 1, 1, 2);
        using var handler = new Handler(path =>
        {
            if (path.EndsWith("/latest")) return JsonSerializer.Serialize(Release(latest));
            if (path.EndsWith("&page=1"))
                return JsonSerializer.Serialize(Enumerable.Repeat(Release(current), 99).Append(Release(latest)));
            Assert.Contains("page=2", path);
            return JsonSerializer.Serialize(new[] { Release(middle), Release(latest), Release(middle, draft: true),
                Release(new Version(current.Major + 1, 2, 1, 1), prerelease: true) });
        });
        using var http = new HttpClient(handler);
        var checker = CreateChecker(http);
        var result = await checker.CheckForUpdateDetailedAsync(ignoreDismissed: true);
        Assert.Equal(ShellUpdateCheckStatus.Available, result.Status);
        Assert.Equal(new[] { latest, middle }, result.Offer!.ReleaseNotes.Select(n => n.Version));
        Assert.All(result.Offer.ReleaseNotes, note => Assert.Contains("Fix the update", note.DisplayBody));
        Assert.Null(result.Offer.ReleaseNotesError);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task HistoryFailurePreservesOfferAndLatestNotesWithVisibleExplanation()
    {
        var latest = new Version(ShellVersionInfo.Current.Major + 1, 1, 1, 1);
        using var handler = new Handler(path => path.EndsWith("/latest")
            ? JsonSerializer.Serialize(Release(latest)) : throw new HttpRequestException("Rate limit exceeded"));
        using var http = new HttpClient(handler);
        var result = await CreateChecker(http).CheckForUpdateDetailedAsync(ignoreDismissed: true);
        Assert.Equal(ShellUpdateCheckStatus.Available, result.Status);
        Assert.Single(result.Offer!.ReleaseNotes);
        Assert.Contains("Rate limit exceeded", result.Offer.ReleaseNotesError);
    }

    [Fact]
    public async Task UpToDateCheckDoesNotFetchReleaseHistory()
    {
        using var handler = new Handler(_ => JsonSerializer.Serialize(Release(ShellVersionInfo.Current)));
        using var http = new HttpClient(handler);
        Assert.Equal(ShellUpdateCheckStatus.UpToDate,
            (await CreateChecker(http).CheckForUpdateDetailedAsync()).Status);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void ReleaseNotesAreReadableWithoutExecutingMarkup()
    {
        Assert.Equal("What's changed\n• Fix the update (#42)",
            ShellReleaseNotesText.ToPlainText("## What's changed\n- **Fix** the `update` ([#42](https://github.com/example/42))"));
        Assert.Contains("No patch notes", ShellReleaseNotesText.ToPlainText(""));
    }

    private static ShellGitHubUpdateChecker CreateChecker(HttpClient http) => new(
        new ShellUpdatePreferences(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), httpClient: http);

    private static object Release(Version version, bool draft = false, bool prerelease = false) => new
    {
        tag_name = "v" + version.ToString(4), name = version.ToString(4), draft, prerelease,
        body = "## What's changed\n- Fix the update", html_url = "https://github.com/example/releases/" + version
    };

    private sealed class Handler(Func<string, string> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request.RequestUri!.PathAndQuery))
            });
        }
    }
}
