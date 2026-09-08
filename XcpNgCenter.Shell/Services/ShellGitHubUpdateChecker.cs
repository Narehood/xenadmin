using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    ShellUpdateAsset? Asset)
{
    public IReadOnlyList<ShellReleaseNotes> ReleaseNotes { get; init; } = [];
    public string? ReleaseNotesError { get; init; }
}

public sealed record ShellReleaseNotes(Version Version, string Body, string HtmlUrl)
{
    public string Heading => $"Changes in {Version.ToString(4)}";
    public string DisplayBody => ShellReleaseNotesText.ToPlainText(Body);
}

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
    private static readonly Lazy<HttpClient> MachineTrustHttp = new(() => CreateClient(machineTrustOnly: true));

    private readonly string _owner;
    private readonly string _repo;
    private readonly ShellUpdatePreferences _preferences;
    private readonly HttpClient _http;

    public ShellGitHubUpdateChecker(ShellUpdatePreferences? preferences = null, string? owner = null, string? repo = null,
        HttpClient? httpClient = null)
    {
        _preferences = preferences ?? new ShellUpdatePreferences();
        _http = httpClient ?? Http;
        ResolveRepo(owner, repo, out _owner, out _repo);
    }

    public string ReleasesPageUrl => $"https://github.com/{_owner}/{_repo}/releases";

    internal static bool IsDefaultRepositoryConfigured
    {
        get
        {
            ResolveRepo(null, null, out var owner, out var repo);
            return string.Equals(owner, DefaultOwner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(repo, DefaultRepo, StringComparison.OrdinalIgnoreCase);
        }
    }

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
            try
            {
                var notes = await FetchReleaseNotesAsync(local, remote, cancellationToken).ConfigureAwait(false);
                if (!notes.Any(note => note.Version == remote))
                    notes = new[] { new ShellReleaseNotes(remote, release.Body ?? string.Empty, url) }.Concat(notes).ToArray();
                offer = offer with { ReleaseNotes = notes };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Notes are informational: their failure must not hide a valid update.
                offer = offer with
                {
                    ReleaseNotes = [new ShellReleaseNotes(remote, release.Body ?? string.Empty, url)],
                    ReleaseNotesError = $"Could not load the full release history: {ex.Message}. View all releases on GitHub."
                };
            }

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

    private async Task<IReadOnlyList<ShellReleaseNotes>> FetchReleaseNotesAsync(
        Version local, Version latest, CancellationToken cancellationToken)
    {
        var notes = new Dictionary<Version, ShellReleaseNotes>();
        for (var page = 1; ; page++)
        {
            var releases = await GetJsonAsync<List<GitHubRelease>>(
                $"https://api.github.com/repos/{_owner}/{_repo}/releases?per_page=100&page={page}",
                cancellationToken, allowNotFound: false, _http).ConfigureAwait(false) ?? [];
            foreach (var release in releases)
            {
                if (release.Draft || release.Prerelease
                    || !ShellVersionInfo.TryParse(release.TagName, out var version)
                    || version <= local || version > latest) continue;
                notes.TryAdd(version, new ShellReleaseNotes(version, release.Body ?? string.Empty,
                    release.HtmlUrl ?? ReleasesPageUrl));
            }
            // GitHub orders by publication date, not version. An older version on
            // this page doesn't prove that later pages contain no newer versions.
            if (releases.Count < 100) break;
        }
        return notes.Values.OrderByDescending(note => note.Version).ToArray();
    }

    internal static async Task<ShellUpdateAsset> GetPublishedAssetAsync(
        Version version, bool useDefaultRepository, CancellationToken cancellationToken)
    {
        var owner = DefaultOwner;
        var repo = DefaultRepo;
        // Elevated code must never take its publisher or digest from writable cache
        // metadata or an environment override inherited from an unelevated process.
        if (!useDefaultRepository)
            ResolveRepo(null, null, out owner, out repo);
        var tag = "v" + version.ToString(4);
        var release = await GetJsonAsync<GitHubRelease>(
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/tags/{Uri.EscapeDataString(tag)}",
            cancellationToken, allowNotFound: false,
            useDefaultRepository && OperatingSystem.IsWindows() ? MachineTrustHttp.Value : Http).ConfigureAwait(false);
        if (release == null || release.Draft || release.Prerelease
            || !string.Equals(release.TagName, tag, StringComparison.Ordinal))
            throw new InvalidDataException("The requested update is not a published stable release.");
        return SelectPlatformAsset(
            release.Assets?.Where(a => a.State == "uploaded").Select(a =>
                new ShellUpdateAsset(a.Name ?? string.Empty, a.DownloadUrl ?? string.Empty, a.Size, a.Digest)) ?? [],
            version, OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), RuntimeInformation.ProcessArchitecture)
            ?? throw new InvalidDataException("The published release has no authenticated package for this platform.");
    }

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
            allowNotFound: true, _http).ConfigureAwait(false);
        if (latest != null)
            return latest;

        var list = await GetJsonAsync<List<GitHubRelease>>(
            $"https://api.github.com/repos/{_owner}/{_repo}/releases?per_page=10",
            cancellationToken,
            allowNotFound: true, _http).ConfigureAwait(false);
        return list?
            .Where(r => r is { Draft: false, Prerelease: false })
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    private static async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken, bool allowNotFound,
        HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await (client ?? Http).SendAsync(request, cancellationToken).ConfigureAwait(false);
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

    private static HttpClient CreateClient(bool machineTrustOnly = false)
    {
        var client = machineTrustOnly
            ? new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                    ValidateMachineTrustCertificate(certificate, chain, errors)
            })
            : new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(12);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"XCP-ng-Center-Shell/{ShellVersionInfo.Display}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    internal static bool ValidateMachineTrustCertificate(X509Certificate2? certificate,
        X509Chain? presentedChain, SslPolicyErrors errors)
    {
        // SslStream checks the hostname and certificate availability. Its default
        // chain can trust current-user roots, so even a reported success must be
        // rebuilt against Windows' machine chain engine before trusting privileged update metadata.
        if (!OperatingSystem.IsWindows() || certificate == null
            || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            return false;

        using var machineChain = new X509Chain(useMachineContext: true);
        try
        {
            var policy = machineChain.ChainPolicy;
            policy.TrustMode = X509ChainTrustMode.System;
            policy.VerificationFlags = X509VerificationFlags.NoFlag;
            policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // TLS server authentication
            if (presentedChain != null)
            {
                // Preserve the transport's revocation/download policy, never its
                // trust anchors or verification exemptions. ExtraStore supplies
                // intermediate candidates; it does not make their roots trusted.
                policy.RevocationMode = presentedChain.ChainPolicy.RevocationMode;
                policy.RevocationFlag = presentedChain.ChainPolicy.RevocationFlag;
                policy.UrlRetrievalTimeout = presentedChain.ChainPolicy.UrlRetrievalTimeout;
                policy.DisableCertificateDownloads = presentedChain.ChainPolicy.DisableCertificateDownloads;
                policy.ExtraStore.AddRange(presentedChain.ChainPolicy.ExtraStore);
                foreach (X509ChainElement element in presentedChain.ChainElements)
                    if (!element.Certificate.Equals(certificate))
                        policy.ExtraStore.Add(element.Certificate);
            }
            return machineChain.Build(certificate);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            foreach (X509ChainElement element in machineChain.ChainElements)
                element.Certificate.Dispose();
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("body")]
        public string? Body { get; set; }

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
