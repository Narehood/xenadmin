using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XenCenterLib.Archive;

namespace XcpNgCenter.Shell.Services;

public sealed record ShellUpdateProgress(long BytesReceived, long TotalBytes, string Status)
{
    public double Percentage => TotalBytes <= 0
        ? 0
        : Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100);
}

public sealed record PreparedShellUpdate(
    Version Version,
    string UpdateRoot,
    string PayloadDirectory,
    string ExecutableName);

/// <summary>
/// Downloads, verifies, and stages a shell release in the current user's local update cache.
/// The staged new executable then waits for this process to exit, replaces the installed
/// files (requesting Windows elevation only when required), and relaunches from the original directory.
/// </summary>
public sealed class ShellUpdateInstaller
{
    private const string UpdateDirectoryName = ".xcpng-update";
    private const string PayloadDirectoryName = "payload";
    private const string ManifestFileName = "update.json";
    private const string ApplyArgument = "--apply-shell-update";
    private const string WaitPidArgument = "--wait-pid";
    private const string InstallDirectoryArgument = "--install-directory";
    private const string UpdateRootArgument = "--update-root";
    private const string CleanupArgument = "--cleanup-shell-update";
    private const string UpdatedVersionArgument = "--updated-to";
    private const string UpdateErrorArgument = "--shell-update-error";
    private const string DeferRestartArgument = "--defer-shell-update-restart";
    private const string RestartBrokerArgument = "--wait-for-shell-update-result";
    private const string UpdateResultFileName = "update-result.json";
    private const int ElevationCancelledError = 1223;
    private const long MaximumExtractedBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumArchiveEntries = 50_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HttpClient Http = CreateClient();
    private static string? _startupStatusMessage;

    private readonly string _installDirectory;
    private readonly string _currentExecutablePath;
    private readonly string _stagingBaseDirectory;
    private readonly bool _isWindows;
    private readonly bool _isLinux;
    private readonly Architecture _architecture;
    private readonly Func<string, bool> _directoryWritableProbe;
    private bool? _installDirectoryWritable;

    public ShellUpdateInstaller()
        : this(
            AppContext.BaseDirectory,
            Environment.ProcessPath ?? string.Empty,
            OperatingSystem.IsWindows(),
            OperatingSystem.IsLinux(),
            RuntimeInformation.ProcessArchitecture,
            stagingRoot: null,
            directoryWritableProbe: null)
    {
    }

    internal ShellUpdateInstaller(
        string installDirectory,
        string currentExecutablePath,
        bool isWindows,
        bool isLinux,
        Architecture architecture,
        string? stagingRoot = null,
        Func<string, bool>? directoryWritableProbe = null)
    {
        _installDirectory = NormalizeDirectory(installDirectory);
        _currentExecutablePath = string.IsNullOrWhiteSpace(currentExecutablePath)
            ? string.Empty
            : Path.GetFullPath(currentExecutablePath);
        _isWindows = isWindows;
        _isLinux = isLinux;
        _architecture = architecture;
        _stagingBaseDirectory = GetStagingBaseDirectory(
            _installDirectory,
            stagingRoot ?? ShellPaths.GetUpdateStagingRoot(ensureExists: false),
            isWindows);
        _directoryWritableProbe = directoryWritableProbe ?? ProbeDirectoryWritable;
    }

    public string InstallDirectory => _installDirectory;

    public string StagingDirectory => _stagingBaseDirectory;

    public bool RequiresElevationForInstall =>
        ShouldRequestElevation(
            _isWindows,
            IsInstallDirectoryWritable(),
            IsProtectedWindowsInstallDirectory(_installDirectory),
            IsCurrentProcessElevated());

    public static string? StartupStatusMessage => _startupStatusMessage;

    public bool CanInstallInPlace(out string reason)
    {
        if ((!_isWindows && !_isLinux) || _architecture != Architecture.X64)
        {
            reason = "Automatic installation is currently available for Windows x64 and Linux x64 builds.";
            return false;
        }

        var expectedExecutable = Path.Combine(_installDirectory, GetExecutableName(_isWindows));
        if (string.IsNullOrWhiteSpace(_currentExecutablePath)
            || !PathEquals(_currentExecutablePath, expectedExecutable)
            || !File.Exists(expectedExecutable))
        {
            reason = "Automatic installation requires the self-contained shell to be running from its published folder.";
            return false;
        }

        if (_isLinux && !IsInstallDirectoryWritable())
        {
            reason = $"The install directory is not writable ({_installDirectory}). Move the portable installation to a user-writable folder or update it with the required system permissions.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal static bool ShouldRequestElevation(
        bool isWindows,
        bool installDirectoryWritable,
        bool isProtectedInstallDirectory = false,
        bool processElevated = false) =>
        isWindows
        && !processElevated
        && (isProtectedInstallDirectory || !installDirectoryWritable);

    /// <summary>
    /// Program Files installs can look writable under UAC VirtualStore while real
    /// files stay protected — always treat those roots as elevation-required.
    /// </summary>
    internal static bool IsProtectedWindowsInstallDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(directory);
        }
        catch
        {
            return false;
        }

        foreach (var root in GetWindowsProtectedInstallRoots())
        {
            if (IsPathUnderRoot(full, root))
                return true;
        }

        return false;
    }

    internal static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> GetWindowsProtectedInstallRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetEnvironmentVariable("ProgramW6432") ?? string.Empty;
        yield return Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty;
    }

    private static bool IsPathUnderRoot(string fullPath, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(root);
        }
        catch
        {
            return false;
        }

        if (string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = Path.TrimEndingDirectorySeparator(normalizedRoot) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public PreparedShellUpdate? TryGetPreparedUpdate(ShellUpdateOffer offer)
    {
        try
        {
            var updateRoot = GetUpdateRoot(offer.Version);
            var manifest = ReadManifest(updateRoot);
            var expectedExecutableName = GetExecutableName(_isWindows);
            if (manifest == null
                || !Version.TryParse(manifest.Version, out var version)
                || version != offer.Version
                || !PathEquals(manifest.InstallDirectory, _installDirectory)
                || !string.Equals(manifest.ExecutableName, expectedExecutableName, StringComparison.Ordinal)
                || (offer.Asset != null
                    && !string.Equals(manifest.AssetName, offer.Asset.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var payload = Path.Combine(updateRoot, PayloadDirectoryName);
            var executable = Path.Combine(payload, manifest.ExecutableName);
            var managedAssembly = Path.Combine(payload, "XcpNgCenter.Shell.dll");
            if (!File.Exists(executable) || !File.Exists(managedAssembly))
                return null;
            ValidatePayload(payload, manifest.ExecutableName, version);

            return new PreparedShellUpdate(version, updateRoot, payload, manifest.ExecutableName);
        }
        catch
        {
            return null;
        }
    }

    public async Task<PreparedShellUpdate> PrepareAsync(
        ShellUpdateOffer offer,
        IProgress<ShellUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (offer.Asset == null)
            throw new InvalidOperationException("This release does not include a compatible update package.");
        if (!CanInstallInPlace(out var reason))
            throw new InvalidOperationException(reason);

        var alreadyPrepared = TryGetPreparedUpdate(offer);
        if (alreadyPrepared != null)
        {
            progress?.Report(new ShellUpdateProgress(offer.Asset.Size, offer.Asset.Size, "Update already downloaded and verified."));
            return alreadyPrepared;
        }

        EnsureStagingDirectoryIsWritable();
        var updateRoot = GetUpdateRoot(offer.Version);
        ResetUpdateRoot(updateRoot);

        var archiveName = Path.GetFileName(offer.Asset.Name);
        if (!string.Equals(archiveName, offer.Asset.Name, StringComparison.Ordinal))
            throw new InvalidDataException("The update asset name is not safe.");

        var partialPath = Path.Combine(updateRoot, archiveName + ".download");
        var archivePath = Path.Combine(updateRoot, archiveName);
        var payloadDirectory = Path.Combine(updateRoot, PayloadDirectoryName);

        try
        {
            await DownloadAsync(offer.Asset, partialPath, progress, cancellationToken).ConfigureAwait(false);
            File.Move(partialPath, archivePath, overwrite: true);

            progress?.Report(new ShellUpdateProgress(offer.Asset.Size, offer.Asset.Size, "Verifying download…"));
            await VerifyDownloadedAssetAsync(archivePath, offer.Asset, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(payloadDirectory);
            progress?.Report(new ShellUpdateProgress(offer.Asset.Size, offer.Asset.Size, "Preparing update…"));
            await ExtractArchiveAsync(archivePath, payloadDirectory, cancellationToken).ConfigureAwait(false);

            var executableName = GetExecutableName(_isWindows);
            ValidatePayload(payloadDirectory, executableName, offer.Version);
            TryDeleteFile(archivePath);

            var manifest = new PreparedManifest
            {
                Version = offer.Version.ToString(4),
                TagName = offer.TagName,
                AssetName = offer.Asset.Name,
                ExecutableName = executableName,
                InstallDirectory = _installDirectory,
                PreparedAt = DateTimeOffset.UtcNow
            };
            WriteManifest(updateRoot, manifest);

            progress?.Report(new ShellUpdateProgress(offer.Asset.Size, offer.Asset.Size, "Ready to restart."));
            return new PreparedShellUpdate(offer.Version, updateRoot, payloadDirectory, executableName);
        }
        catch
        {
            TryDeleteDirectory(updateRoot);
            throw;
        }
    }

    public void DiscardPreparedUpdate(PreparedShellUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var expectedRoot = GetUpdateRoot(update.Version);
        if (PathEquals(update.UpdateRoot, expectedRoot))
            TryDeleteDirectory(expectedRoot);
    }

    public void StartApplyHelper(PreparedShellUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var expectedRoot = GetUpdateRoot(update.Version);
        if (!PathEquals(update.UpdateRoot, expectedRoot)
            || !string.Equals(update.ExecutableName, GetExecutableName(_isWindows), StringComparison.Ordinal))
            throw new InvalidOperationException("The prepared update directory is invalid.");

        var stagedExecutable = Path.Combine(update.PayloadDirectory, update.ExecutableName);
        if (!File.Exists(stagedExecutable))
            throw new FileNotFoundException("The staged updater executable is missing.", stagedExecutable);

        var requiresElevation = RequiresElevationForInstall;
        Process? restartBroker = null;
        var previousResult = Path.Combine(update.UpdateRoot, UpdateResultFileName);
        if (File.Exists(previousResult))
            File.Delete(previousResult);
        try
        {
            if (requiresElevation)
                restartBroker = StartRestartBroker(stagedExecutable, update, Environment.ProcessId, _installDirectory);

            var startInfo = CreateApplyStartInfo(
                stagedExecutable,
                update.PayloadDirectory,
                Environment.ProcessId,
                _installDirectory,
                update.UpdateRoot,
                requiresElevation,
                deferRestart: requiresElevation);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The update helper could not be started.");
        }
        catch (Win32Exception ex) when (requiresElevation && ex.NativeErrorCode == ElevationCancelledError)
        {
            TryStopProcess(restartBroker);
            throw new OperationCanceledException(
                "Administrator approval was cancelled. The verified update is still ready to install.", ex);
        }
        catch
        {
            TryStopProcess(restartBroker);
            throw;
        }
        finally
        {
            restartBroker?.Dispose();
        }
    }

    internal static ProcessStartInfo CreateApplyStartInfo(
        string stagedExecutable,
        string payloadDirectory,
        int parentProcessId,
        string installDirectory,
        string updateRoot,
        bool elevate,
        bool deferRestart)
    {
        if (deferRestart && !elevate)
            throw new ArgumentException("Deferred restart requires an elevated apply helper.", nameof(deferRestart));

        var startInfo = new ProcessStartInfo
        {
            FileName = stagedExecutable,
            WorkingDirectory = payloadDirectory,
            UseShellExecute = elevate
        };
        if (elevate)
            startInfo.Verb = "runas";
        startInfo.ArgumentList.Add(ApplyArgument);
        startInfo.ArgumentList.Add(WaitPidArgument);
        startInfo.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(InstallDirectoryArgument);
        startInfo.ArgumentList.Add(installDirectory);
        startInfo.ArgumentList.Add(UpdateRootArgument);
        startInfo.ArgumentList.Add(updateRoot);
        if (deferRestart)
            startInfo.ArgumentList.Add(DeferRestartArgument);
        return startInfo;
    }

    private static Process StartRestartBroker(
        string stagedExecutable,
        PreparedShellUpdate update,
        int parentProcessId,
        string installDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = stagedExecutable,
            WorkingDirectory = update.PayloadDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(RestartBrokerArgument);
        startInfo.ArgumentList.Add(WaitPidArgument);
        startInfo.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(InstallDirectoryArgument);
        startInfo.ArgumentList.Add(installDirectory);
        startInfo.ArgumentList.Add(UpdateRootArgument);
        startInfo.ArgumentList.Add(update.UpdateRoot);

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("The non-elevated update restart helper could not be started.");
    }

    /// <summary>
    /// Waits outside the elevated process and relaunches the installed app with the
    /// original user's token after a protected-directory update finishes.
    /// </summary>
    public static bool TryRunRestartBrokerMode(string[] args, out int exitCode)
    {
        if (!args.Contains(RestartBrokerArgument, StringComparer.Ordinal))
        {
            exitCode = 0;
            return false;
        }

        var waitPid = 0;
        string? installDirectory = null;
        string? updateRoot = null;
        try
        {
            var waitPidText = GetArgumentValue(args, WaitPidArgument)
                ?? throw new InvalidDataException("The update restart helper is missing the parent process ID.");
            if (!int.TryParse(waitPidText, out waitPid) || waitPid <= 0 || waitPid == Environment.ProcessId)
                throw new InvalidDataException("The update restart helper parent process ID is invalid.");

            installDirectory = NormalizeDirectory(GetArgumentValue(args, InstallDirectoryArgument)
                ?? throw new InvalidDataException("The update restart helper is missing the install directory."));
            updateRoot = NormalizeDirectory(GetArgumentValue(args, UpdateRootArgument)
                ?? throw new InvalidDataException("The update restart helper is missing the staging directory."));
            ValidatePreparedUpdateLocation(installDirectory, updateRoot);

            var manifest = ReadManifest(updateRoot)
                ?? throw new InvalidDataException("The prepared update manifest is missing.");
            if (!PathEquals(manifest.InstallDirectory, installDirectory)
                || !string.Equals(
                    manifest.ExecutableName,
                    GetExecutableName(OperatingSystem.IsWindows()),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The prepared update does not match this installation.");
            }
            var stagedExecutable = Path.Combine(updateRoot, PayloadDirectoryName, manifest.ExecutableName);
            if (!PathEquals(Environment.ProcessPath ?? string.Empty, stagedExecutable))
                throw new InvalidDataException("The update restart helper is not running from the prepared package.");

            WaitForProcessExit(waitPid);
            var result = WaitForUpdateResult(updateRoot);
            if (string.IsNullOrWhiteSpace(result.ErrorMessage)
                && !string.Equals(result.Version, manifest.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The update completion result has an unexpected version.");
            }
            var executable = Path.Combine(installDirectory, GetExecutableName(OperatingSystem.IsWindows()));
            if (!TryRestartApplication(
                    executable,
                    installDirectory,
                    updateRoot,
                    result.Version,
                    result.ErrorMessage))
            {
                throw new InvalidOperationException("XCP-ng Center could not be restarted after the update.");
            }

            exitCode = string.IsNullOrWhiteSpace(result.ErrorMessage) ? 0 : 1;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Shell update restart helper failed: {ex}");
            if (waitPid > 0 && !string.IsNullOrWhiteSpace(installDirectory))
            {
                try
                {
                    WaitForProcessExit(waitPid);
                    var executable = Path.Combine(installDirectory, GetExecutableName(OperatingSystem.IsWindows()));
                    TryRestartApplication(
                        executable,
                        installDirectory,
                        updateRoot ?? string.Empty,
                        string.Empty,
                        $"The update could not be completed: {ex.Message}");
                }
                catch
                {
                    // The broker failure remains the useful diagnostic.
                }
            }
            exitCode = 1;
        }

        return true;
    }

    /// <summary>Runs the headless update helper before Avalonia is initialized.</summary>
    public static bool TryRunApplyMode(string[] args, out int exitCode)
    {
        if (!args.Contains(ApplyArgument, StringComparer.Ordinal))
        {
            exitCode = 0;
            return false;
        }

        var waitPid = 0;
        string? installDirectory = null;
        string? updateRoot = null;
        var deferRestart = args.Contains(DeferRestartArgument, StringComparer.Ordinal);
        try
        {
            var waitPidText = GetArgumentValue(args, WaitPidArgument)
                ?? throw new InvalidDataException("The update helper is missing the parent process ID.");
            if (!int.TryParse(waitPidText, out waitPid) || waitPid <= 0 || waitPid == Environment.ProcessId)
                throw new InvalidDataException("The update helper parent process ID is invalid.");

            installDirectory = NormalizeDirectory(GetArgumentValue(args, InstallDirectoryArgument)
                ?? throw new InvalidDataException("The update helper is missing the install directory."));
            updateRoot = NormalizeDirectory(GetArgumentValue(args, UpdateRootArgument)
                ?? throw new InvalidDataException("The update helper is missing the staging directory."));

            exitCode = ApplyPreparedUpdate(waitPid, installDirectory, updateRoot, deferRestart);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Shell update helper failed: {ex}");
            if (deferRestart && !string.IsNullOrWhiteSpace(updateRoot))
            {
                TryWriteUpdateResult(
                    updateRoot!,
                    string.Empty,
                    $"The update could not be installed: {ex.Message}");
            }
            else if (waitPid > 0 && !string.IsNullOrWhiteSpace(installDirectory))
            {
                try
                {
                    WaitForProcessExit(waitPid);
                    var executable = Path.Combine(installDirectory, GetExecutableName(OperatingSystem.IsWindows()));
                    TryRestartApplication(
                        executable,
                        installDirectory,
                        updateRoot ?? string.Empty,
                        string.Empty,
                        $"The update could not be installed: {ex.Message}");
                }
                catch
                {
                    // The original helper failure remains the useful diagnostic.
                }
            }
            exitCode = 1;
        }

        return true;
    }

    /// <summary>Consumes updater-only startup flags before passing arguments to Avalonia.</summary>
    public static string[] PrepareApplicationStartup(string[] args)
    {
        var remaining = new List<string>(args.Length);
        string? cleanupDirectory = null;
        string? updatedVersion = null;
        string? updateError = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is CleanupArgument or UpdatedVersionArgument or UpdateErrorArgument)
            {
                if (i + 1 < args.Length)
                {
                    var value = args[++i];
                    if (args[i - 1] == CleanupArgument)
                        cleanupDirectory = value;
                    else if (args[i - 1] == UpdatedVersionArgument)
                        updatedVersion = value;
                    else
                        updateError = value;
                }
                continue;
            }

            remaining.Add(args[i]);
        }

        if (!string.IsNullOrWhiteSpace(updatedVersion))
            _startupStatusMessage = $"Updated successfully to {updatedVersion}.";
        else if (!string.IsNullOrWhiteSpace(updateError))
            _startupStatusMessage = updateError;

        if (!string.IsNullOrWhiteSpace(cleanupDirectory) && IsSafeCleanupDirectory(cleanupDirectory))
            ScheduleCleanup(cleanupDirectory);

        return remaining.ToArray();
    }

    internal static async Task VerifyDownloadedAssetAsync(
        string path,
        ShellUpdateAsset asset,
        CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (asset.Size > 0 && length != asset.Size)
            throw new InvalidDataException($"The update download is incomplete ({length:N0} of {asset.Size:N0} bytes).");

        if (string.IsNullOrWhiteSpace(asset.Digest))
            return;

        var parts = asset.Digest.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !string.Equals(parts[0], "sha256", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The release uses an unsupported download digest.");

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actual, parts[1], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
    }

    internal static async Task ExtractArchiveAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractZipAsync(archivePath, destinationDirectory, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractTarGzAsync(archivePath, destinationDirectory, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new InvalidDataException("The update package format is not supported.");
    }

    private static async Task DownloadAsync(
        ShellUpdateAsset asset,
        string destinationPath,
        IProgress<ShellUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseLength = response.Content.Headers.ContentLength ?? 0;
        if (responseLength > 0 && asset.Size > 0 && responseLength != asset.Size)
            throw new InvalidDataException("The release server reported an unexpected update size.");

        var total = asset.Size > 0 ? asset.Size : responseLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[128 * 1024];
        long received = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            progress?.Report(new ShellUpdateProgress(received, total, "Downloading update…"));
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractZipAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        long extractedBytes = 0;
        var entries = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GuardArchiveLimits(++entries, ref extractedBytes, entry.Length);

            // Unix file type 0120000 denotes a symbolic link in ZIP external attributes.
            if ((((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000))
                throw new InvalidDataException($"Update archive link '{entry.FullName}' is not allowed.");

            var name = NormalizeArchiveEntryName(entry.FullName);
            if (string.IsNullOrEmpty(name))
                continue;
            var destinationPath = ArchivePath.GetSafeExtractPath(destinationDirectory, name);
            if (string.IsNullOrEmpty(entry.Name) || name.EndsWith('/') || name.EndsWith('\\'))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var source = entry.Open();
            await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExtractTarGzAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
        using var reader = new TarReader(gzip, leaveOpen: false);

        long extractedBytes = 0;
        var entries = 0;
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GuardArchiveLimits(++entries, ref extractedBytes, entry.Length);

            var name = NormalizeArchiveEntryName(entry.Name);
            if (string.IsNullOrEmpty(name))
                continue;
            var destinationPath = ArchivePath.GetSafeExtractPath(destinationDirectory, name);

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException($"Update archive entry '{entry.Name}' has an unsupported type.");

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (entry.DataStream != null)
                    await entry.DataStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(destinationPath, entry.Mode);
        }
    }

    private static int ApplyPreparedUpdate(
        int waitPid,
        string installDirectory,
        string updateRoot,
        bool deferRestart)
    {
        ValidatePreparedUpdateLocation(installDirectory, updateRoot);

        var manifest = ReadManifest(updateRoot)
            ?? throw new InvalidDataException("The prepared update manifest is missing.");
        if (!PathEquals(manifest.InstallDirectory, installDirectory))
            throw new InvalidDataException("The prepared update targets a different installation.");
        var expectedExecutableName = GetExecutableName(OperatingSystem.IsWindows());
        if (!string.Equals(manifest.ExecutableName, expectedExecutableName, StringComparison.Ordinal))
            throw new InvalidDataException("The prepared update executable name is invalid.");

        var payloadDirectory = NormalizeDirectory(Path.Combine(updateRoot, PayloadDirectoryName));
        var stagedExecutable = Path.Combine(payloadDirectory, manifest.ExecutableName);
        var processPath = Environment.ProcessPath ?? string.Empty;
        if (!PathEquals(processPath, stagedExecutable))
            throw new InvalidDataException("The update helper is not running from the prepared package.");

        WaitForProcessExit(waitPid);

        string? errorMessage = null;
        try
        {
            if (!Version.TryParse(manifest.Version, out var expectedVersion))
                throw new InvalidDataException("The prepared update version is invalid.");
            ApplyPayload(payloadDirectory, installDirectory, Path.Combine(updateRoot, "backup"), expectedVersion);
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(updateRoot, "update-error.log");
            TryWriteAllText(logPath, ex.ToString());
            errorMessage = $"The update could not be installed. Recovery details: {logPath}";
        }

        var installedExecutable = Path.Combine(installDirectory, manifest.ExecutableName);
        if (deferRestart)
        {
            if (!TryWriteUpdateResult(updateRoot, manifest.Version, errorMessage))
                return 1;
            return errorMessage == null ? 0 : 1;
        }

        if (!TryRestartApplication(installedExecutable, installDirectory, updateRoot, manifest.Version, errorMessage))
            return 1;
        return errorMessage == null ? 0 : 1;
    }

    internal static void ApplyPayload(
        string payloadDirectory,
        string installDirectory,
        string backupDirectory,
        Version? expectedVersion = null)
    {
        ValidatePayload(payloadDirectory, GetExecutableName(OperatingSystem.IsWindows()), expectedVersion);
        TryDeleteDirectory(backupDirectory);
        Directory.CreateDirectory(backupDirectory);

        var files = Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories).ToList();
        if (files.Count == 0 || files.Count > MaximumArchiveEntries)
            throw new InvalidDataException("The prepared update contains an invalid number of files.");

        var existing = new HashSet<string>(GetPathComparer());
        var applied = new List<string>();
        foreach (var source in files)
        {
            RejectLink(source);
            var relative = Path.GetRelativePath(payloadDirectory, source);
            RejectReservedUpdatePath(relative);
            var target = ArchivePath.GetSafeExtractPath(installDirectory, relative);
            if (!File.Exists(target))
                continue;

            existing.Add(relative);
            var backup = ArchivePath.GetSafeExtractPath(backupDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            CopyFilePreservingMode(target, backup, overwrite: true);
        }

        try
        {
            foreach (var source in files)
            {
                var relative = Path.GetRelativePath(payloadDirectory, source);
                var target = ArchivePath.GetSafeExtractPath(installDirectory, relative);
                CopyFileAtomicallyWithRetry(source, target);
                applied.Add(relative);
            }
        }
        catch
        {
            foreach (var relative in Enumerable.Reverse(applied))
            {
                try
                {
                    var target = ArchivePath.GetSafeExtractPath(installDirectory, relative);
                    if (existing.Contains(relative))
                    {
                        var backup = ArchivePath.GetSafeExtractPath(backupDirectory, relative);
                        CopyFileAtomicallyWithRetry(backup, target);
                    }
                    else
                    {
                        TryDeleteFile(target);
                    }
                }
                catch
                {
                    // Continue restoring every file; the original exception is more useful to the user.
                }
            }

            throw;
        }
    }

    private static bool TryRestartApplication(
        string executable,
        string installDirectory,
        string updateRoot,
        string version,
        string? errorMessage)
    {
        try
        {
            if (!File.Exists(executable))
                return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = installDirectory,
                UseShellExecute = false
            };
            if (errorMessage == null)
            {
                startInfo.ArgumentList.Add(CleanupArgument);
                startInfo.ArgumentList.Add(updateRoot);
                startInfo.ArgumentList.Add(UpdatedVersionArgument);
                startInfo.ArgumentList.Add(version);
            }
            else
            {
                startInfo.ArgumentList.Add(UpdateErrorArgument);
                startInfo.ArgumentList.Add(errorMessage);
            }

            using var process = Process.Start(startInfo);
            return process != null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not restart XCP-ng Center after update: {ex}");
            return false;
        }
    }

    private static void WaitForProcessExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited && !process.WaitForExit(120_000))
                throw new TimeoutException("XCP-ng Center did not exit in time for the update.");
        }
        catch (ArgumentException)
        {
            // The process exited before the helper opened it.
        }

        Thread.Sleep(250);
    }

    private static UpdateResult WaitForUpdateResult(string updateRoot)
    {
        var path = Path.Combine(updateRoot, UpdateResultFileName);
        for (var attempt = 0; attempt < 720; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    var result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(path));
                    if (result != null)
                        return result;
                }
            }
            catch (IOException)
            {
                // The elevated helper may still be committing the result.
            }
            catch (JsonException)
            {
                // The elevated helper may still be committing the result.
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("The elevated update helper did not report completion in time.");
    }

    private static bool TryWriteUpdateResult(string updateRoot, string version, string? errorMessage)
    {
        try
        {
            var path = Path.Combine(updateRoot, UpdateResultFileName);
            var temporary = path + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new UpdateResult
            {
                Version = version,
                ErrorMessage = errorMessage
            }, JsonOptions));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not write shell update result: {ex}");
            return false;
        }
    }

    private static void TryStopProcess(Process? process)
    {
        if (process == null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup after a failed or cancelled elevation request.
        }
    }

    private static void ScheduleCleanup(string directory)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000).ConfigureAwait(false);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                            return;
                        Directory.Delete(directory, recursive: true);
                    }
                    return;
                }
                catch
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        });
    }

    private static bool IsSafeCleanupDirectory(string directory)
    {
        try
        {
            var normalized = NormalizeDirectory(directory);
            var updateBase = GetStagingBaseDirectory(
                AppContext.BaseDirectory,
                ShellPaths.GetUpdateStagingRoot(ensureExists: false),
                OperatingSystem.IsWindows());
            return PathEquals(Path.GetDirectoryName(normalized) ?? string.Empty, updateBase)
                   && Path.GetFileName(normalized).StartsWith('v');
        }
        catch
        {
            return false;
        }
    }

    private void EnsureStagingDirectoryIsWritable()
    {
        Directory.CreateDirectory(_stagingBaseDirectory);
        var probe = Path.Combine(_stagingBaseDirectory, $"write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.WriteByte(0);
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                $"The update staging directory is not writable ({_stagingBaseDirectory}).",
                ex);
        }
        finally
        {
            TryDeleteFile(probe);
        }
    }

    private bool IsInstallDirectoryWritable() =>
        _installDirectoryWritable ??= _directoryWritableProbe(_installDirectory);

    private static bool ProbeDirectoryWritable(string directory)
    {
        if (!Directory.Exists(directory))
            return false;

        var probe = Path.Combine(directory, $".xcpng-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.WriteByte(0);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            TryDeleteFile(probe);
        }
    }

    private string GetUpdateRoot(Version version) =>
        NormalizeDirectory(Path.Combine(_stagingBaseDirectory, $"v{version.ToString(4)}"));

    private static void ResetUpdateRoot(string updateRoot)
    {
        TryDeleteDirectory(updateRoot);
        Directory.CreateDirectory(updateRoot);
    }

    private static void ValidatePayload(string payloadDirectory, string executableName, Version? expectedVersion = null)
    {
        var executable = Path.Combine(payloadDirectory, executableName);
        var managedAssembly = Path.Combine(payloadDirectory, "XcpNgCenter.Shell.dll");
        var runtimeConfig = Path.Combine(payloadDirectory, "XcpNgCenter.Shell.runtimeconfig.json");
        if (!File.Exists(executable) || !File.Exists(managedAssembly) || !File.Exists(runtimeConfig))
            throw new InvalidDataException("The update package is missing required shell files.");
        RejectLink(executable);
        RejectLink(managedAssembly);
        RejectLink(runtimeConfig);

        if (expectedVersion != null)
        {
            var assemblyVersion = AssemblyName.GetAssemblyName(managedAssembly).Version;
            if (assemblyVersion != expectedVersion)
                throw new InvalidDataException(
                    $"The update package version ({assemblyVersion}) does not match the release ({expectedVersion.ToString(4)}).");
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Update package link '{path}' is not allowed.");
    }

    private static void RejectReservedUpdatePath(string relativePath)
    {
        var first = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (string.Equals(first, UpdateDirectoryName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update package contains a reserved staging path.");
    }

    private static void GuardArchiveLimits(int entries, ref long extractedBytes, long entryLength)
    {
        if (entries > MaximumArchiveEntries || entryLength < 0)
            throw new InvalidDataException("The update archive exceeds safe extraction limits.");
        checked
        {
            extractedBytes += entryLength;
        }
        if (extractedBytes > MaximumExtractedBytes)
            throw new InvalidDataException("The update archive is unexpectedly large when extracted.");
    }

    private static string NormalizeArchiveEntryName(string name)
    {
        var normalized = name;
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized is "." or "" ? string.Empty : normalized;
    }

    private static void CopyFileAtomicallyWithRetry(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Exception? lastError = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var temporary = Path.Combine(
                Path.GetDirectoryName(target)!,
                $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.xcpng-new");
            try
            {
                CopyFilePreservingMode(source, temporary, overwrite: false);
                File.Move(temporary, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                TryDeleteFile(temporary);
                if (attempt < 39)
                    Thread.Sleep(250);
            }
        }

        throw new IOException($"Could not replace '{target}'.", lastError);
    }

    private static void CopyFilePreservingMode(string source, string destination, bool overwrite)
    {
        File.Copy(source, destination, overwrite);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    private static PreparedManifest? ReadManifest(string updateRoot)
    {
        try
        {
            var path = Path.Combine(updateRoot, ManifestFileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<PreparedManifest>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteManifest(string updateRoot, PreparedManifest manifest)
    {
        var path = Path.Combine(updateRoot, ManifestFileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static string? GetArgumentValue(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        }
        return null;
    }

    private static string GetExecutableName(bool isWindows) =>
        isWindows ? "XcpNgCenter.Shell.exe" : "XcpNgCenter.Shell";

    internal static string GetStagingBaseDirectory(
        string installDirectory,
        string stagingRoot,
        bool isWindows)
    {
        var identity = GetInstallDirectoryIdentity(installDirectory, isWindows);
        return NormalizeDirectory(Path.Combine(stagingRoot, identity));
    }

    private static string GetInstallDirectoryIdentity(string installDirectory, bool isWindows)
    {
        var normalized = NormalizeDirectory(installDirectory);
        if (isWindows)
            normalized = normalized.ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static void ValidatePreparedUpdateLocation(string installDirectory, string updateRoot)
    {
        var normalizedRoot = NormalizeDirectory(updateRoot);
        var updateBase = Path.GetDirectoryName(normalizedRoot) ?? string.Empty;
        var expectedIdentity = GetInstallDirectoryIdentity(installDirectory, OperatingSystem.IsWindows());
        if (!string.Equals(Path.GetFileName(updateBase), expectedIdentity, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(normalizedRoot).StartsWith('v'))
        {
            throw new InvalidDataException("The update staging directory does not match this installation.");
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var isLink = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
                Directory.Delete(path, recursive: !isLink);
            }
        }
        catch
        {
            // A later create/copy will surface a useful error if cleanup was required.
        }
    }

    private static void TryWriteAllText(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text);
        }
        catch
        {
            // Best-effort diagnostics.
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"XCP-ng-Center-Shell/{ShellVersionInfo.Display}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return client;
    }

    private sealed class UpdateResult
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }

    private sealed class PreparedManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("tagName")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("assetName")]
        public string AssetName { get; set; } = string.Empty;

        [JsonPropertyName("executableName")]
        public string ExecutableName { get; set; } = string.Empty;

        [JsonPropertyName("installDirectory")]
        public string InstallDirectory { get; set; } = string.Empty;

        [JsonPropertyName("preparedAt")]
        public DateTimeOffset PreparedAt { get; set; }
    }
}
