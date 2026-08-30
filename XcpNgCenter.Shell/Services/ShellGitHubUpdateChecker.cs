using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XcpNgCenter.Shell.Services;

public sealed record ShellUpdateAsset(
    string Name,
    string DownloadUrl,
    long Size,
    string? Digest);

public sealed record ShellUpdateOffer(
    Version Version,
    string TagName,
    string Title,
    string HtmlUrl,
    DateTimeOffset? PublishedAt,
    ShellUpdateAsset? Asset);

public enum ShellUpdateCheckStatus
{
    UpToDate,
    Available,
    Dismissed,
    Failed
}

public sealed record ShellUpdateCheckResult(
    ShellUpdateCheckStatus Status,
    ShellUpdateOffer? Offer = null,
    string? Detail = null);

/// <summary>
/// Checks GitHub Releases for a newer client build.
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

    /// <summary>
    /// Silent startup-friendly check. Prefer <see cref="CheckForUpdateDetailedAsync"/> when the
    /// UI must distinguish failures and dismissed updates from a true "up to date".
    /// </summary>
    public async Task<ShellUpdateOffer?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var result = await CheckForUpdateDetailedAsync(ignoreDismissed: false, cancellationToken)
            .ConfigureAwait(false);
        return result.Status == ShellUpdateCheckStatus.Available ? result.Offer : null;
    }

    public async Task<ShellUpdateCheckResult> CheckForUpdateDetailedAsync(
        bool ignoreDismissed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await FetchLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (release == null || release.Draft || release.Prerelease)
            {
                return new ShellUpdateCheckResult(
                    ShellUpdateCheckStatus.UpToDate,
                    Detail: "No newer published release was found on GitHub.");
            }

            if (!ShellVersionInfo.TryParse(release.TagName, out var remote)
                && !ShellVersionInfo.TryParse(release.Name, out remote))
            {
                return new ShellUpdateCheckResult(
                    ShellUpdateCheckStatus.Failed,
                    Detail: $"Could not parse the latest release version ({release.TagName ?? release.Name}).");
            }

            var local = ShellVersionInfo.Current;
            if (remote.CompareTo(local) <= 0)
            {
                return new ShellUpdateCheckResult(
                    ShellUpdateCheckStatus.UpToDate,
                    Detail: $"You're up to date ({ShellVersionInfo.Display}).");
            }

            var url = string.IsNullOrWhiteSpace(release.HtmlUrl) ? ReleasesPageUrl : release.HtmlUrl!;
            var title = string.IsNullOrWhiteSpace(release.Name) ? (release.TagName ?? remote.ToString(4)) : release.Name!;
            var tag = release.TagName ?? remote.ToString(4);
            var asset = SelectPlatformAsset(
                release.Assets?
                    .Where(static item => string.Equals(item.State, "uploaded", StringComparison.OrdinalIgnoreCase))
                    .Select(static item => new ShellUpdateAsset(
                        item.Name ?? string.Empty,
                        item.DownloadUrl ?? string.Empty,
                        item.Size,
                        item.Digest)) ?? [],
                remote,
                OperatingSystem.IsWindows(),
                OperatingSystem.IsLinux(),
                RuntimeInformation.ProcessArchitecture);
            var offer = new ShellUpdateOffer(remote, tag, title, url, release.PublishedAt, asset);

            var dismissed = _preferences.GetDismissedVersion();
            if (!ignoreDismissed
                && !string.IsNullOrWhiteSpace(dismissed)
                && ShellVersionInfo.TryParse(dismissed, out var dismissedVersion)
                && dismissedVersion.CompareTo(remote) >= 0)
            {
                return new ShellUpdateCheckResult(
                    ShellUpdateCheckStatus.Dismissed,
                    offer,
                    $"Update {remote.ToString(4)} is available but was previously dismissed.");
            }

            return new ShellUpdateCheckResult(ShellUpdateCheckStatus.Available, offer);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ShellUpdateCheckResult(
                ShellUpdateCheckStatus.Failed,
                Detail: ex.Message);
        }
    }

    public void Dismiss(Version version) => _preferences.SetDismissedVersion(version.ToString(4));

    public void ClearDismissed() => _preferences.ClearDismissedVersion();

    internal static ShellUpdateAsset? SelectPlatformAsset(
        IEnumerable<ShellUpdateAsset> assets,
        Version version,
        bool isWindows,
        bool isLinux,
        Architecture architecture)
    {
        if (architecture != Architecture.X64)
            return null;

        var expectedName = isWindows
            ? $"XcpNgCenter.Shell-win-x64-{version.ToString(4)}.zip"
            : isLinux
                ? $"XcpNgCenter.Shell-linux-x64-{version.ToString(4)}.tar.gz"
                : null;
        if (expectedName == null)
            return null;

        return assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase)
            && asset.Size > 0
            && IsSha256Digest(asset.Digest)
            && Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return false;
        var parts = digest.Split(':', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && string.Equals(parts[0], "sha256", StringComparison.OrdinalIgnoreCase)
               && parts[1].Length == 64
               && parts[1].All(Uri.IsHexDigit);
    }

    private async Task<GitHubRelease?> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        // Prefer /latest (non-draft, non-prerelease). Fall back to listing when empty.
        var latest = await GetJsonAsync<GitHubRelease>(
            $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest",
            cancellationToken,
            allowNotFound: true).ConfigureAwait(false);
        if (latest != null)
            return latest;

        var list = await GetJsonAsync<List<GitHubRelease>>(
            $"https://api.github.com/repos/{_owner}/{_repo}/releases?per_page=10",
            cancellationToken,
            allowNotFound: true).ConfigureAwait(false);
        return list?
            .Where(r => r is { Draft: false, Prerelease: false })
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    private static async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken, bool allowNotFound)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && allowNotFound)
            return default;
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub Releases returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

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

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }
    }
}
