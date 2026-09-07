using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ShellUpdateBootstrapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BootstrapRefusesToLaunchCachedCode(bool elevate)
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => ShellUpdateInstaller.CreateBootstrapStartInfo(
            Path.Combine(fixture.Cache, fixture.Executable), fixture.Install, fixture.Cache,
            fixture.Launch, fixture.Version, 123, elevate));
    }

    [Fact]
    public async Task LaunchPayloadComesFromAuthenticatedArchive_NotCachedExecutableOrManifest()
    {
        using var fixture = new Fixture();
        var asset = fixture.CreateArchive();
        Directory.CreateDirectory(Path.Combine(fixture.Cache, "payload"));
        File.WriteAllText(Path.Combine(fixture.Cache, "payload", fixture.Executable), "modified cached executable");
        File.WriteAllText(Path.Combine(fixture.Cache, "update.json"), "untrusted cached metadata");

        await fixture.Prepare(asset);

        Assert.Equal("verified executable", File.ReadAllText(Path.Combine(fixture.Launch, "payload", fixture.Executable)));
        Assert.Contains(asset.Digest!, File.ReadAllText(Path.Combine(fixture.Launch, "update.json")));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Install));
    }

    [Fact]
    public async Task ChangedArchiveIsRejectedBeforeExtractionOrInstallation()
    {
        using var fixture = new Fixture();
        var asset = fixture.CreateArchive();
        var archivePath = Path.Combine(fixture.Cache, asset.Name);
        var bytes = File.ReadAllBytes(archivePath);
        bytes[bytes.Length / 2] ^= 0x40;
        File.WriteAllBytes(archivePath, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(asset));
        Assert.False(Directory.Exists(Path.Combine(fixture.Launch, "payload")));
        Assert.False(File.Exists(Path.Combine(fixture.Launch, "update.json")));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Install));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:aa")]
    public async Task MissingOrMalformedDigestCannotAuthorizeAnArchive(string? digest)
    {
        using var fixture = new Fixture();
        var asset = fixture.CreateArchive() with { Digest = digest };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(asset));
        Assert.False(Directory.Exists(Path.Combine(fixture.Launch, "payload")));
    }

    [Fact]
    public async Task WrongAssemblyVersionCannotBecomeLaunchable()
    {
        using var fixture = new Fixture();
        var asset = fixture.CreateArchive();
        await Assert.ThrowsAsync<InvalidDataException>(() => ShellUpdateInstaller.PrepareLaunchPayloadAsync(
            fixture.Cache, fixture.Launch, fixture.Install, new Version(1, 0, 0, 0), asset, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(fixture.Launch, "update.json")));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Install));
    }

    [Fact]
    public async Task ExistingLaunchArchiveCannotBeOverwritten()
    {
        using var fixture = new Fixture();
        var asset = fixture.CreateArchive();
        File.WriteAllText(Path.Combine(fixture.Launch, asset.Name), "existing");
        await Assert.ThrowsAsync<IOException>(() => fixture.Prepare(asset));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(fixture.Launch, asset.Name)));
    }

    [Fact]
    public async Task FailedPreparationCleanupPreservesDownloadAndInstallation()
    {
        using var fixture = new Fixture();
        var asset = fixture.CreateArchive();
        await Assert.ThrowsAsync<InvalidDataException>(() => ShellUpdateInstaller.PrepareLaunchPayloadAsync(
            fixture.Cache, fixture.Launch, fixture.Install, new Version(1, 0, 0, 0), asset, CancellationToken.None));
        Assert.True(Directory.Exists(Path.Combine(fixture.Launch, "payload")));

        ShellUpdateInstaller.RemoveUnlaunchedPayload(fixture.Launch);

        Assert.Empty(Directory.GetFileSystemEntries(fixture.Launch));
        Assert.True(File.Exists(Path.Combine(fixture.Cache, asset.Name)));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Install));
    }

    [Fact]
    public async Task MissingBrokerAcknowledgementNeverAuthorizesInstallation()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<TimeoutException>(() => ShellUpdateInstaller.WaitForRestartBrokerAsync(
            fixture.Launch, fixture.Cache, TimeSpan.FromMilliseconds(20), _ =>
                throw new InvalidOperationException("No process should be inspected without an acknowledgement.")));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Install));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadOrDifferentBrokerCannotAuthorizeInstallation(bool dead)
    {
        using var fixture = new Fixture();
        fixture.AcknowledgeBroker();
        await Assert.ThrowsAsync<InvalidDataException>(() => ShellUpdateInstaller.WaitForRestartBrokerAsync(
            fixture.Launch, fixture.Cache, TimeSpan.FromSeconds(1), _ => dead ? null : Path.Combine(fixture.Cache, fixture.Executable)));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Install));
    }

    [Fact]
    public async Task InstallationWaitsForLateBrokerAcknowledgement()
    {
        using var fixture = new Fixture();
        var waiting = ShellUpdateInstaller.WaitForRestartBrokerAsync(fixture.Launch, fixture.Cache,
            TimeSpan.FromSeconds(5), pid => pid == 123 ? Path.Combine(fixture.Launch, "payload", fixture.Executable) : null);
        Assert.False(waiting.IsCompleted);
        fixture.AcknowledgeBroker();
        await waiting;
    }

    [Fact]
    public void InvalidHeadlessApplyArgumentsDoNotWriteFailureFiles()
    {
        using var fixture = new Fixture();
        Assert.True(ShellUpdateInstaller.TryRunApplyMode(
            ["--apply-shell-update", "--wait-pid", "1", "--install-directory", fixture.Install,
                "--update-root", fixture.Cache, "--defer-shell-update-restart"], out var exit));
        Assert.Equal(1, exit);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Cache));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Install));
    }

    [Fact]
    public void ProtectedStagingDescriptor_HasAdministrativeOwnerAndNoUserWriteRights()
    {
        if (!OperatingSystem.IsWindows()) return;
        var acl = ShellUpdateInstaller.CreateProtectedDirectorySecurity();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        Assert.Equal(admins, acl.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(acl.AreAccessRulesProtected);
        var rules = new List<(string Sid, FileSystemRights Rights, AccessControlType Type)>();
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            rules.Add((rule.IdentityReference.Value, rule.FileSystemRights, rule.AccessControlType));
        var usersSid = users.Value;
        var adminsSid = admins.Value;
        var systemSid = system.Value;
        Assert.Contains(rules, r => r.Sid == usersSid
            && (r.Rights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute);
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        Assert.DoesNotContain(rules, r => r.Type == AccessControlType.Allow
            && (r.Rights & writes) != 0 && r.Sid != adminsSid && r.Sid != systemSid);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "xcpng-bootstrap-tests-" + Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            Cache = Path.Combine(_root, "cache");
            Launch = Path.Combine(_root, "launch");
            Install = Path.Combine(_root, "install");
            foreach (var path in new[] { Cache, Launch, Install }) Directory.CreateDirectory(path);
        }
        public string Cache { get; }
        public string Launch { get; }
        public string Install { get; }
        public string Executable => OperatingSystem.IsWindows() ? "XcpNgCenter.Shell.exe" : "XcpNgCenter.Shell";
        public Version Version => typeof(ShellUpdateInstaller).Assembly.GetName().Version!;
        public Task Prepare(ShellUpdateAsset asset) => ShellUpdateInstaller.PrepareLaunchPayloadAsync(
            Cache, Launch, Install, Version, asset, CancellationToken.None);
        public void AcknowledgeBroker() =>
            File.WriteAllText(Path.Combine(Cache, "." + Path.GetFileName(Launch) + ".broker-ready"), "123");
        public ShellUpdateAsset CreateArchive()
        {
            var path = Path.Combine(Cache, "package.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry(Executable).Open())) writer.Write("verified executable");
                archive.CreateEntryFromFile(typeof(ShellUpdateInstaller).Assembly.Location, "XcpNgCenter.Shell.dll");
                using var config = new StreamWriter(archive.CreateEntry("XcpNgCenter.Shell.runtimeconfig.json").Open());
                config.Write("{}");
            }
            return new ShellUpdateAsset("package.zip", "https://github.com/example/release", new FileInfo(path).Length,
                "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
