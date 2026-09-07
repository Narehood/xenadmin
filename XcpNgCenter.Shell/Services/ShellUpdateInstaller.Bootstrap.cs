using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using XenCenterLib.Archive;

namespace XcpNgCenter.Shell.Services;

public sealed partial class ShellUpdateInstaller
{
    private const string BootstrapArgument = "--prepare-shell-update";
    private const string LaunchRootArgument = "--launch-root";
    private const string VersionArgument = "--release-version";
    private const string ProtectedCleanupArgument = "--cleanup-protected-update";
    private const string ProtectedLaunchPrefix = ".xcpng-update-";
    private const string LaunchPrefix = "launch-";
    private const string BootstrapReadyFile = "bootstrap-ready";
    private const string BootstrapErrorFile = "bootstrap-error.txt";

    internal static ProcessStartInfo CreateBootstrapStartInfo(
        string installedExecutable, string installDirectory, string cacheRoot,
        string launchRoot, Version version, int parentPid, bool elevate)
    {
        if (!PathEquals(installedExecutable, Path.Combine(installDirectory, GetExecutableName(OperatingSystem.IsWindows()))))
            throw new InvalidDataException("Update elevation must start from the installed executable.");
        var info = new ProcessStartInfo
        {
            FileName = installedExecutable,
            WorkingDirectory = installDirectory,
            UseShellExecute = elevate,
            CreateNoWindow = !elevate
        };
        if (elevate) info.Verb = "runas";
        foreach (var value in new[] { BootstrapArgument, WaitPidArgument, parentPid.ToString(),
                     InstallDirectoryArgument, installDirectory, UpdateRootArgument, cacheRoot,
                     LaunchRootArgument, launchRoot, VersionArgument, version.ToString(4) })
            info.ArgumentList.Add(value);
        return info;
    }

    private static string CreateLaunchRootPath(string installDirectory, bool protectedLaunch) =>
        protectedLaunch
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProtectedLaunchPrefix + Guid.NewGuid().ToString("N"))
            : Path.Combine(GetStagingBaseDirectory(installDirectory, ShellPaths.GetUpdateStagingRoot(false), OperatingSystem.IsWindows()),
                LaunchPrefix + Guid.NewGuid().ToString("N"));

    private static bool HasRandomDirectoryName(string path, string prefix)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(prefix, StringComparison.Ordinal)
               && Guid.TryParseExact(name[prefix.Length..], "N", out _);
    }

    private static bool IsProtectedLaunchPath(string path) =>
        OperatingSystem.IsWindows()
        && PathEquals(Path.GetDirectoryName(NormalizeDirectory(path)) ?? string.Empty,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
        && HasRandomDirectoryName(NormalizeDirectory(path), ProtectedLaunchPrefix);

    private static void ValidateLaunchRoot(string installDirectory, string launchRoot, bool requireProtected)
    {
        var root = NormalizeDirectory(launchRoot);
        if (OperatingSystem.IsWindows() && IsProtectedLaunchPath(root))
        {
            RejectPathLinks(root);
            if (Directory.Exists(root))
                ValidateProtectedDirectory(root);
            return;
        }
        var expectedBase = GetStagingBaseDirectory(installDirectory, ShellPaths.GetUpdateStagingRoot(false), OperatingSystem.IsWindows());
        if (requireProtected || !HasRandomDirectoryName(root, LaunchPrefix)
            || !PathEquals(Path.GetDirectoryName(root) ?? string.Empty, expectedBase))
            throw new InvalidDataException("The helper launch directory is invalid.");
        RejectPathLinks(root);
    }

    internal static void RejectPathLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update paths must not contain symbolic links or junctions.");
            current = Path.GetDirectoryName(current);
        }
    }

    internal static DirectorySecurity CreateProtectedDirectorySecurity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void ValidateProtectedDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (!admins.Equals(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected)
            throw new InvalidDataException("The update staging directory is not administrator-owned and protected.");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0
                && !rule.IdentityReference.Equals(admins) && !rule.IdentityReference.Equals(system))
                throw new InvalidDataException("The update staging directory allows unprivileged modification.");
    }

    private static void CreateLaunchDirectory(string installDirectory, string launchRoot, bool elevated)
    {
        ValidateLaunchRoot(installDirectory, launchRoot, elevated);
        if (Directory.Exists(launchRoot) || File.Exists(launchRoot))
            throw new InvalidDataException("The helper launch directory already exists.");
        if (elevated && OperatingSystem.IsWindows())
        {
            // Children inherit this DACL. Require the token's default owner to be an
            // administrative SID too, so an unelevated user cannot rewrite child ACLs.
            using var identity = WindowsIdentity.GetCurrent();
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (identity.Owner is not { } owner || (!admins.Equals(owner)
                && !new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Equals(owner)))
                throw new InvalidOperationException("The installer token cannot create administrator-owned staging files.");
            new DirectoryInfo(launchRoot).Create(CreateProtectedDirectorySecurity());
            ValidateProtectedDirectory(launchRoot);
        }
        else if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(launchRoot);
        else
            Directory.CreateDirectory(launchRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Runs only installed code, before initializing UI or user TLS settings.</summary>
    public static bool TryRunBootstrapMode(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!args.Contains(BootstrapArgument, StringComparer.Ordinal)) return false;
        string? launchRoot = null;
        var ownsLaunchRoot = false;
        Process? helper = null;
        try
        {
            var install = NormalizeDirectory(GetArgumentValue(args, InstallDirectoryArgument)
                ?? throw new InvalidDataException("Missing installation directory."));
            if (!PathEquals(Environment.ProcessPath ?? string.Empty, Path.Combine(install, GetExecutableName(OperatingSystem.IsWindows()))))
                throw new InvalidDataException("The bootstrap is not running from the installation.");
            RejectPathLinks(install);
            var cacheRoot = NormalizeDirectory(GetArgumentValue(args, UpdateRootArgument)
                ?? throw new InvalidDataException("Missing downloaded package directory."));
            var version = Version.Parse(GetArgumentValue(args, VersionArgument)
                ?? throw new InvalidDataException("Missing release version."));
            if (version <= ShellVersionInfo.Current || version.Revision < 0)
                throw new InvalidDataException("The requested release is not newer than the installed application.");
            // UAC may use a different administrator account. The cache is read-only
            // input, authenticated below, and need not be under that account's profile.
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(cacheRoot)),
                    GetInstallDirectoryIdentity(install, OperatingSystem.IsWindows()), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(cacheRoot), "v" + version.ToString(4), StringComparison.Ordinal))
                throw new InvalidDataException("The package cache does not match this installation and version.");
            var parentPid = int.Parse(GetArgumentValue(args, WaitPidArgument)
                ?? throw new InvalidDataException("Missing parent process."));
            if (parentPid <= 0 || parentPid == Environment.ProcessId) throw new InvalidDataException("Invalid parent process.");
            launchRoot = NormalizeDirectory(GetArgumentValue(args, LaunchRootArgument)
                ?? throw new InvalidDataException("Missing helper launch directory."));
            var elevated = OperatingSystem.IsWindows() && IsCurrentProcessElevated();
            CreateLaunchDirectory(install, launchRoot, elevated);
            ownsLaunchRoot = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var asset = ShellGitHubUpdateChecker.GetPublishedAssetAsync(version, elevated, timeout.Token).GetAwaiter().GetResult();
            PrepareLaunchPayloadAsync(cacheRoot, launchRoot, install, version, asset, timeout.Token).GetAwaiter().GetResult();
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(launchRoot, PayloadDirectoryName, GetExecutableName(OperatingSystem.IsWindows())),
                WorkingDirectory = Path.Combine(launchRoot, PayloadDirectoryName),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in new[] { ApplyArgument, WaitPidArgument, parentPid.ToString(), InstallDirectoryArgument,
                         install, UpdateRootArgument, launchRoot, DeferRestartArgument })
                info.ArgumentList.Add(arg);
            helper = Process.Start(info) ?? throw new InvalidOperationException("Could not start the verified update helper.");
            File.WriteAllText(Path.Combine(launchRoot, BootstrapReadyFile), "ready");
        }
        catch (Exception ex)
        {
            if (ownsLaunchRoot && launchRoot != null)
            {
                // No process can be using these partial files until the helper starts.
                // Retain only the error marker for the waiting application. If a
                // helper did start, leave its files and possible rollback data intact.
                if (helper == null)
                    RemoveUnlaunchedPayload(launchRoot);
                TryWriteAllText(Path.Combine(launchRoot, BootstrapErrorFile), ex.Message);
            }
            Trace.WriteLine($"Update bootstrap failed: {ex}");
            exitCode = 1;
        }
        finally
        {
            helper?.Dispose();
        }
        return true;
    }

    internal static void RemoveUnlaunchedPayload(string ownedLaunchRoot)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(ownedLaunchRoot))
                TryDeleteDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(ownedLaunchRoot))
                TryDeleteFile(file);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to clean partial update staging: {ex.Message}");
        }
    }

    internal static async Task PrepareLaunchPayloadAsync(string cacheRoot, string launchRoot,
        string installDirectory, Version version, ShellUpdateAsset authenticatedAsset, CancellationToken cancellationToken)
    {
        // Only the independently fetched release supplies the name, size, and digest.
        // Copy first, then verify the private copy; never extract or execute cached code.
        var sourcePath = ArchivePath.GetSafeExtractPath(cacheRoot, authenticatedAsset.Name);
        RejectPathLinks(sourcePath);
        var archivePath = ArchivePath.GetSafeExtractPath(launchRoot, authenticatedAsset.Name);
        if (authenticatedAsset.Size <= 0 || authenticatedAsset.Size > MaximumExtractedBytes)
            throw new InvalidDataException("The update archive size is invalid.");
        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var target = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                total += read;
                if (total > authenticatedAsset.Size) throw new InvalidDataException("The update archive size changed.");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        await VerifyDownloadedAssetAsync(archivePath, authenticatedAsset, cancellationToken).ConfigureAwait(false);
        var payload = Path.Combine(launchRoot, PayloadDirectoryName);
        Directory.CreateDirectory(payload);
        await ExtractArchiveAsync(archivePath, payload, cancellationToken).ConfigureAwait(false);
        var executable = GetExecutableName(OperatingSystem.IsWindows());
        NormalizePortablePackageLayout(payload, executable);
        ValidatePayload(payload, executable, version);
        WriteManifest(launchRoot, new PreparedManifest
        {
            Version = version.ToString(4), TagName = "v" + version.ToString(4),
            AssetName = authenticatedAsset.Name, Digest = authenticatedAsset.Digest!,
            ExecutableName = executable, InstallDirectory = installDirectory,
            PreparedAt = DateTimeOffset.UtcNow, CacheRoot = cacheRoot
        });
        TryDeleteFile(archivePath);
    }

    private static async Task WaitForBootstrapAsync(Process bootstrap, string launchRoot)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(Path.Combine(launchRoot, BootstrapReadyFile))) return;
            if (bootstrap.HasExited)
            {
                var errorPath = Path.Combine(launchRoot, BootstrapErrorFile);
                throw new InvalidOperationException(File.Exists(errorPath)
                    ? File.ReadAllText(errorPath) : "The installed updater could not prepare a verified package.");
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        throw new TimeoutException("The updater did not finish verifying the package in time.");
    }

    private static string GetBrokerAcknowledgementPath(string cacheRoot, string launchRoot) =>
        ArchivePath.GetSafeExtractPath(cacheRoot, "." + Path.GetFileName(launchRoot) + ".broker-ready");

    private static void WaitForRestartBroker(string launchRoot, string? cacheRoot)
        => WaitForRestartBrokerAsync(launchRoot, cacheRoot, TimeSpan.FromMinutes(1), GetLiveProcessExecutable)
            .GetAwaiter().GetResult();

    private static string? GetLiveProcessExecutable(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : process.MainModule?.FileName;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    internal static async Task WaitForRestartBrokerAsync(string launchRoot, string? cacheRoot,
        TimeSpan timeout, Func<int, string?> resolveExecutable)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
            throw new InvalidDataException("The update has no restart-broker acknowledgement path.");
        var acknowledgement = GetBrokerAcknowledgementPath(cacheRoot, launchRoot);
        var brokerExecutable = Path.Combine(launchRoot, PayloadDirectoryName, GetExecutableName(OperatingSystem.IsWindows()));
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            int? brokerPid = null;
            try
            {
                if (File.Exists(acknowledgement))
                {
                    // The acknowledgement is untrusted input from the original user.
                    // Read a bounded PID, never an arbitrarily large cache file.
                    using var reader = new StreamReader(acknowledgement);
                    var buffer = new char[12];
                    var count = reader.ReadBlock(buffer, 0, buffer.Length);
                    if (count < buffer.Length && int.TryParse(buffer.AsSpan(0, count), out var parsed))
                        brokerPid = parsed;
                }
            }
            catch (IOException) { /* The parent may still be committing the acknowledgement. */ }
            if (brokerPid is { } pid)
            {
                var executable = pid > 0 ? resolveExecutable(pid) : null;
                if (string.IsNullOrWhiteSpace(executable) || !PathEquals(executable, brokerExecutable))
                    throw new InvalidDataException("The restart broker does not match the verified package.");
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(1, (timeout - elapsed.Elapsed).TotalMilliseconds))))
                .ConfigureAwait(false);
        }
        throw new TimeoutException("The restart broker was not started; the installation has not been changed.");
    }

    private static void StartProtectedCleanup(string installDirectory, string launchRoot)
    {
        if (!IsProtectedLaunchPath(launchRoot) || !IsCurrentProcessElevated()) return;
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(installDirectory, GetExecutableName(true)),
            WorkingDirectory = installDirectory, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var arg in new[] { ProtectedCleanupArgument, launchRoot, WaitPidArgument, Environment.ProcessId.ToString() })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info);
    }

    public static bool TryRunProtectedCleanupMode(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!args.Contains(ProtectedCleanupArgument, StringComparer.Ordinal)) return false;
        try
        {
            var root = NormalizeDirectory(GetArgumentValue(args, ProtectedCleanupArgument)
                ?? throw new InvalidDataException("Missing cleanup directory."));
            if (!IsCurrentProcessElevated() || !IsProtectedLaunchPath(root))
                throw new InvalidDataException("Invalid protected cleanup directory.");
            RejectPathLinks(root);
            ValidateProtectedDirectory(root);
            var manifest = ReadManifest(root) ?? throw new InvalidDataException("Missing update manifest.");
            if (!PathEquals(Environment.ProcessPath ?? string.Empty, Path.Combine(manifest.InstallDirectory, GetExecutableName(true))))
                throw new InvalidDataException("Cleanup is not running from the updated installation.");
            var pid = int.Parse(GetArgumentValue(args, WaitPidArgument) ?? "0");
            if (pid <= 0 || pid == Environment.ProcessId) throw new InvalidDataException("Invalid cleanup wait process.");
            WaitForProcessExit(pid);
            // Recursive deletion may remove the result before encountering a locked
            // executable. Wait for all staged processes before touching any files.
            foreach (var candidate in Process.GetProcessesByName("XcpNgCenter.Shell"))
            {
                using (candidate)
                {
                    var executable = candidate.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executable)
                        && PathEquals(executable, Path.Combine(root, PayloadDirectoryName, GetExecutableName(true))))
                        WaitForProcessExit(candidate.Id);
                }
            }
            for (var attempt = 0; attempt < 60; attempt++)
            {
                try { Directory.Delete(root, recursive: true); return true; }
                catch (IOException) { Thread.Sleep(500); }
                catch (UnauthorizedAccessException) { Thread.Sleep(500); }
            }
            exitCode = 1;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Protected update cleanup failed: {ex}");
            exitCode = 1;
        }
        return true;
    }
}
