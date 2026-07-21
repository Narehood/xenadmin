using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XcpNgCenter.Shell.Services;

public sealed record ShellUpdateOffer(
    Version Version,
    string TagName,
    string Title,
    string HtmlUrl,
    DateTimeOffset? PublishedAt);

/// <summary>
/// Checks GitHub Releases for a newer client build. Failures are silent (offline / no releases yet).
/// </summary>
public sealed class ShellGitHubUpdateChecker
{
    /// <summary>Fork that publishes preview shell builds. Override via env XCPNG_UPDATE_GITHUB_REPO=owner/name.</summary>
    public const string DefaultOwner = "Narehood";
    public const string DefaultRepo = "xenadmin";

    private static readonly HttpClient Http = CreateClient();

    private readonly string _owner;
    private readonly string _repo;
    private readonly ShellUpdatePreferences _preferences;

    public ShellGitHubUpdateChecker(ShellUpdatePreferences? preferences = null, string? owner = null, string? repo = null)
    {
        _preferences = preferences ?? new ShellUpdatePreferences();
        ResolveRepo(owner, repo, out _owner, out _repo);
    }

    public string ReleasesPageUrl => $"https://github.com/{_owner}/{_repo}/releases";

    public async Task<ShellUpdateOffer?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await FetchLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (release == null || release.Draft || release.Prerelease)
                return null;

            if (!ShellVersionInfo.TryParse(release.TagName, out var remote)
                && !ShellVersionInfo.TryParse(release.Name, out remote))
                return null;

            var local = ShellVersionInfo.Current;
            if (remote.CompareTo(local) <= 0)
                return null;

            var dismissed = _preferences.GetDismissedVersion();
            if (!string.IsNullOrWhiteSpace(dismissed)
                && ShellVersionInfo.TryParse(dismissed, out var dismissedVersion)
                && dismissedVersion.CompareTo(remote) >= 0)
                return null;

            var url = string.IsNullOrWhiteSpace(release.HtmlUrl) ? ReleasesPageUrl : release.HtmlUrl!;
            var title = string.IsNullOrWhiteSpace(release.Name) ? (release.TagName ?? remote.ToString(4)) : release.Name!;
            var tag = release.TagName ?? remote.ToString(4);
            return new ShellUpdateOffer(remote, tag, title, url, release.PublishedAt);
        }
        catch
        {
            return null;
        }
    }

    public void Dismiss(Version version) => _preferences.SetDismissedVersion(version.ToString(4));

    private async Task<GitHubRelease?> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        // Prefer /latest (non-draft, non-prerelease). Fall back to listing when empty.
        var latest = await GetJsonAsync<GitHubRelease>(
            $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest",
            cancellationToken).ConfigureAwait(false);
        if (latest != null)
            return latest;

        var list = await GetJsonAsync<List<GitHubRelease>>(
            $"https://api.github.com/repos/{_owner}/{_repo}/releases?per_page=10",
            cancellationToken).ConfigureAwait(false);
        return list?
            .Where(r => r is { Draft: false, Prerelease: false })
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    private static async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return default;
        if (!response.IsSuccessStatusCode)
            return default;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void ResolveRepo(string? owner, string? repo, out string resolvedOwner, out string resolvedRepo)
    {
        var env = Environment.GetEnvironmentVariable("XCPNG_UPDATE_GITHUB_REPO");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var parts = env.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                resolvedOwner = parts[0];
                resolvedRepo = parts[1];
                return;
            }
        }

        resolvedOwner = string.IsNullOrWhiteSpace(owner) ? DefaultOwner : owner!;
        resolvedRepo = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo!;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"XCP-ng-Center-Shell/{ShellVersionInfo.Display}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }
    }
}
