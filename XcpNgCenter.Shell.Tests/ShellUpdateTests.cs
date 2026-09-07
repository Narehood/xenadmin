using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ShellUpdateTests
{
    private static readonly Version ReleaseVersion = new(2026, 8, 14, 2);
    private static readonly string ValidDigest = "sha256:" + new string('a', 64);

    [Fact]
    public void SelectPlatformAsset_UsesExactWindowsPackage()
    {
        var expected = new ShellUpdateAsset(
            "XcpNgCenter.Shell-win-x64-2026.8.14.2.zip",
            "https://github.com/Narehood/xenadmin/releases/download/v2026.8.14.2/update.zip",
            123,
            ValidDigest);
        var other = expected with { Name = "XcpNgCenter.Shell-linux-x64-2026.8.14.2.tar.gz" };

        var selected = ShellGitHubUpdateChecker.SelectPlatformAsset(
            [other, expected], ReleaseVersion, isWindows: true, isLinux: false, Architecture.X64);

        Assert.Equal(expected, selected);
    }

    [Fact]
    public void SelectPlatformAsset_RequiresGitHubSha256Digest()
    {
        var asset = new ShellUpdateAsset(
            "XcpNgCenter.Shell-linux-x64-2026.8.14.2.tar.gz",
            "https://github.com/Narehood/xenadmin/releases/download/v2026.8.14.2/update.tar.gz",
            123,
            null);

        var selected = ShellGitHubUpdateChecker.SelectPlatformAsset(
            [asset], ReleaseVersion, isWindows: false, isLinux: true, Architecture.X64);

        Assert.Null(selected);
    }

    [Fact]
    public void SelectPlatformAsset_UsesExactLinuxPackage()
    {
        var expected = new ShellUpdateAsset(
            "XcpNgCenter.Shell-linux-x64-2026.8.14.2.tar.gz",
            "https://github.com/Narehood/xenadmin/releases/download/v2026.8.14.2/update.tar.gz",
            123,
            ValidDigest);

        var selected = ShellGitHubUpdateChecker.SelectPlatformAsset(
            [expected], ReleaseVersion, isWindows: false, isLinux: true, Architecture.X64);

        Assert.Equal(expected, selected);
    }

    [Fact]
    public void SelectPlatformAsset_RejectsUnsupportedArchitecture()
    {
        var asset = new ShellUpdateAsset(
            "XcpNgCenter.Shell-win-x64-2026.8.14.2.zip",
            "https://github.com/Narehood/xenadmin/releases/download/v2026.8.14.2/update.zip",
            123,
            ValidDigest);

        var selected = ShellGitHubUpdateChecker.SelectPlatformAsset(
            [asset], ReleaseVersion, isWindows: true, isLinux: false, Architecture.Arm64);

        Assert.Null(selected);
    }

    [Theory]
    [InlineData(true, false, false, false, true)]  // protected probe miss
    [InlineData(true, true, false, false, false)] // writable non-protected
    [InlineData(true, true, true, false, true)]   // Program Files + VirtualStore false positive
    [InlineData(true, false, true, false, true)]  // Program Files not writable
    [InlineData(true, false, true, true, false)]  // already elevated
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    public void ShouldRequestElevation_OnlyForUnelevatedProtectedOrUnwritableWindowsInstall(
        bool isWindows,
        bool installDirectoryWritable,
        bool isProtectedInstallDirectory,
        bool processElevated,
        bool expected)
    {
        Assert.Equal(
            expected,
            ShellUpdateInstaller.ShouldRequestElevation(
                isWindows,
                installDirectoryWritable,
                isProtectedInstallDirectory,
                processElevated));
    }

    [Theory]
    [InlineData(@"C:\Program Files\XCP-ng Center", true)]
    [InlineData(@"C:\Program Files (x86)\XCP-ng Center", true)]
    [InlineData(@"C:\Tools\XCP-ng Center", false)]
    [InlineData(@"C:\Users\Michael\Apps\XCP-ng Center", false)]
    public void IsProtectedWindowsInstallDirectory_DetectsProgramFilesRoots(string path, bool expected)
    {
        Assert.Equal(expected, ShellUpdateInstaller.IsProtectedWindowsInstallDirectory(path));
    }

    [Fact]
    public void ProtectedWindowsInstall_RemainsEligibleForAutomaticUpdate()
    {
        using var temp = new TemporaryDirectory();
        var install = Path.Combine(temp.Path, "install");
        var staging = Path.Combine(temp.Path, "staging");
        Directory.CreateDirectory(install);
        var executable = Path.Combine(install, "XcpNgCenter.Shell.exe");
        File.WriteAllText(executable, "shell");

        var installer = new ShellUpdateInstaller(
            install,
            executable,
            isWindows: true,
            isLinux: false,
            Architecture.X64,
            staging,
            _ => false,
            processElevatedProbe: () => false);

        Assert.True(installer.CanInstallInPlace(out var reason), reason);
        Assert.True(installer.RequiresElevationForInstall);
        Assert.StartsWith(Path.GetFullPath(staging), installer.StagingDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VirtualStoreWritableProgramFiles_StillRequiresElevation()
    {
        // UAC VirtualStore can make a create-file probe succeed under Program Files.
        Assert.True(ShellUpdateInstaller.IsProtectedWindowsInstallDirectory(@"C:\Program Files\XCP-ng Center"));
        Assert.True(ShellUpdateInstaller.ShouldRequestElevation(
            isWindows: true,
            installDirectoryWritable: true,
            isProtectedInstallDirectory: true,
            processElevated: false));
    }

    [Fact]
    public void ProtectedLinuxInstall_ExplainsWhyAutomaticUpdateIsUnavailable()
    {
        using var temp = new TemporaryDirectory();
        var install = Path.Combine(temp.Path, "install");
        Directory.CreateDirectory(install);
        var executable = Path.Combine(install, "XcpNgCenter.Shell");
        File.WriteAllText(executable, "shell");

        var installer = new ShellUpdateInstaller(
            install,
            executable,
            isWindows: false,
            isLinux: true,
            Architecture.X64,
            Path.Combine(temp.Path, "staging"),
            _ => false);

        Assert.False(installer.CanInstallInPlace(out var reason));
        Assert.Contains("not writable", reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(installer.RequiresElevationForInstall);
    }

    [Fact]
    public void StagingDirectory_IsStablePerInstallAndWindowsCaseInsensitive()
    {
        using var temp = new TemporaryDirectory();
        var first = ShellUpdateInstaller.GetStagingBaseDirectory(
            @"C:\Program Files\XCP-ng Center",
            temp.Path,
            isWindows: true);
        var same = ShellUpdateInstaller.GetStagingBaseDirectory(
            @"c:\program files\xcp-NG center\",
            temp.Path,
            isWindows: true);
        var other = ShellUpdateInstaller.GetStagingBaseDirectory(
            @"C:\Tools\XCP-ng Center",
            temp.Path,
            isWindows: true);

        Assert.Equal(first, same, ignoreCase: true);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void ResolveInstallDirectory_PrefersProcessPathFolder()
    {
        var resolved = ShellUpdateInstaller.ResolveInstallDirectory(
            @"C:\Wrong\Base",
            @"C:\Program Files\XCP-ng Center\XcpNgCenter.Shell.exe");
        Assert.Equal(
            Path.GetFullPath(@"C:\Program Files\XCP-ng Center"),
            Path.GetFullPath(resolved),
            ignoreCase: true);
    }

    [Fact]
    public void CreateBootstrapStartInfo_ElevatesInstalledCode()
    {
        var info = ShellUpdateInstaller.CreateBootstrapStartInfo(
            @"C:\Program Files\XCP-ng Center\XcpNgCenter.Shell.exe",
            @"C:\Program Files\XCP-ng Center",
            @"C:\staging\v2026.8.14.4",
            @"C:\Program Files\.xcpng-update-0123456789abcdef0123456789abcdef",
            ReleaseVersion, 1234, elevate: true);

        Assert.True(info.UseShellExecute);
        Assert.Equal("runas", info.Verb);
        Assert.Equal(@"C:\Program Files\XCP-ng Center\XcpNgCenter.Shell.exe", info.FileName);
        Assert.Contains("--prepare-shell-update", info.ArgumentList);
        Assert.DoesNotContain("--apply-shell-update", info.ArgumentList);
    }

    [Fact]
    public void CreateBootstrapStartInfo_UsesInstalledCodeWithoutElevationForWritableInstall()
    {
        var info = ShellUpdateInstaller.CreateBootstrapStartInfo(
            @"C:\Tools\XCP-ng Center\XcpNgCenter.Shell.exe",
            @"C:\Tools\XCP-ng Center",
            @"C:\staging\v2026.8.14.4",
            @"C:\staging\launch-0123456789abcdef0123456789abcdef",
            ReleaseVersion, 1234, elevate: false);

        Assert.False(info.UseShellExecute);
        Assert.Empty(info.Verb);
        Assert.DoesNotContain("--defer-shell-update-restart", info.ArgumentList);
    }

    [Theory]
    [InlineData("https://example.com/update.zip")]
    [InlineData("http://github.com/Narehood/xenadmin/update.zip")]
    public void SelectPlatformAsset_RejectsUntrustedDownloadUrl(string url)
    {
        var asset = new ShellUpdateAsset(
            "XcpNgCenter.Shell-win-x64-2026.8.14.2.zip",
            url,
            123,
            ValidDigest);

        var selected = ShellGitHubUpdateChecker.SelectPlatformAsset(
            [asset], ReleaseVersion, isWindows: true, isLinux: false, Architecture.X64);

        Assert.Null(selected);
    }

    [Fact]
    public async Task VerifyDownloadedAssetAsync_AcceptsMatchingSha256()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "update.zip");
        var bytes = "verified update bytes"u8.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes));
        var asset = new ShellUpdateAsset("update.zip", "https://github.com/update.zip", bytes.Length, digest);

        await ShellUpdateInstaller.VerifyDownloadedAssetAsync(path, asset, CancellationToken.None);
    }

    [Fact]
    public async Task VerifyDownloadedAssetAsync_RejectsTamperedFile()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "update.zip");
        var bytes = "tampered update bytes"u8.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        var asset = new ShellUpdateAsset("update.zip", "https://github.com/update.zip", bytes.Length, ValidDigest);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ShellUpdateInstaller.VerifyDownloadedAssetAsync(path, asset, CancellationToken.None));
    }

    [Fact]
    public async Task ExtractArchiveAsync_ExtractsSafeZipEntry()
    {
        using var temp = new TemporaryDirectory();
        var archivePath = Path.Combine(temp.Path, "update.zip");
        var destination = Path.Combine(temp.Path, "payload");
        CreateZip(archivePath, "nested/file.txt", "content");

        await ShellUpdateInstaller.ExtractArchiveAsync(archivePath, destination, CancellationToken.None);

        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(destination, "nested", "file.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsync_RejectsZipTraversal()
    {
        using var temp = new TemporaryDirectory();
        var archivePath = Path.Combine(temp.Path, "update.zip");
        var destination = Path.Combine(temp.Path, "payload");
        CreateZip(archivePath, "../escape.txt", "nope");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ShellUpdateInstaller.ExtractArchiveAsync(archivePath, destination, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(temp.Path, "escape.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsync_RejectsZipSymbolicLink()
    {
        using var temp = new TemporaryDirectory();
        var archivePath = Path.Combine(temp.Path, "update.zip");
        await using (var file = File.Create(archivePath))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link");
            entry.ExternalAttributes = unchecked((int)0xA0000000);
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("target");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ShellUpdateInstaller.ExtractArchiveAsync(
                archivePath,
                Path.Combine(temp.Path, "payload"),
            CancellationToken.None));
    }

    [Fact]
    public async Task ExtractArchiveAsync_ExtractsSafeTarGzEntry()
    {
        using var temp = new TemporaryDirectory();
        var archivePath = Path.Combine(temp.Path, "update.tar.gz");
        var destination = Path.Combine(temp.Path, "payload");
        CreateTarGz(archivePath, new PaxTarEntry(TarEntryType.RegularFile, "nested/file.txt")
        {
            DataStream = new MemoryStream("content"u8.ToArray())
        });

        await ShellUpdateInstaller.ExtractArchiveAsync(archivePath, destination, CancellationToken.None);

        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(destination, "nested", "file.txt")));
    }

    [Fact]
    public void NormalizePortablePackageLayout_UnwrapsAppFolderAndDropsInstallTxt()
    {
        using var temp = new TemporaryDirectory();
        var payload = Path.Combine(temp.Path, "payload");
        var appDir = Path.Combine(payload, "XcpNgCenter.Shell");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(payload, "INSTALL.TXT"), "portable install help");
        CreateMinimumPayload(appDir);
        var executableName = OperatingSystem.IsWindows() ? "XcpNgCenter.Shell.exe" : "XcpNgCenter.Shell";

        ShellUpdateInstaller.NormalizePortablePackageLayout(payload, executableName);

        Assert.True(File.Exists(Path.Combine(payload, executableName)));
        Assert.True(File.Exists(Path.Combine(payload, "XcpNgCenter.Shell.dll")));
        Assert.False(File.Exists(Path.Combine(payload, "INSTALL.TXT")));
        Assert.False(Directory.Exists(appDir));
    }

    [Fact]
    public void NormalizePortablePackageLayout_LeavesFlatPayloadAlone()
    {
        using var temp = new TemporaryDirectory();
        var payload = Path.Combine(temp.Path, "payload");
        Directory.CreateDirectory(payload);
        CreateMinimumPayload(payload);
        File.WriteAllText(Path.Combine(payload, "INSTALL.TXT"), "should be removed");
        var executableName = OperatingSystem.IsWindows() ? "XcpNgCenter.Shell.exe" : "XcpNgCenter.Shell";

        ShellUpdateInstaller.NormalizePortablePackageLayout(payload, executableName);

        Assert.True(File.Exists(Path.Combine(payload, executableName)));
        Assert.False(File.Exists(Path.Combine(payload, "INSTALL.TXT")));
    }

    [Fact]
    public void NormalizePortablePackageLayout_RejectsAmbiguousLayout()
    {
        using var temp = new TemporaryDirectory();
        var payload = Path.Combine(temp.Path, "payload");
        Directory.CreateDirectory(payload);
        var executableName = OperatingSystem.IsWindows() ? "XcpNgCenter.Shell.exe" : "XcpNgCenter.Shell";
        foreach (var folder in new[] { "one", "two" })
        {
            var dir = Path.Combine(payload, folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, executableName), "exe");
        }

        Assert.Throws<InvalidDataException>(() =>
            ShellUpdateInstaller.NormalizePortablePackageLayout(payload, executableName));
    }

    [Fact]
    public async Task ExtractArchiveAsync_RejectsTarSymbolicLink()
    {
        using var temp = new TemporaryDirectory();
        var archivePath = Path.Combine(temp.Path, "update.tar.gz");
        var destination = Path.Combine(temp.Path, "payload");
        CreateTarGz(archivePath, new PaxTarEntry(TarEntryType.SymbolicLink, "link")
        {
            LinkName = "../escape"
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ShellUpdateInstaller.ExtractArchiveAsync(archivePath, destination, CancellationToken.None));
    }

    [Fact]
    public void ApplyPayload_ReplacesExistingFilesAndKeepsBackup()
    {
        using var temp = new TemporaryDirectory();
        var payload = Path.Combine(temp.Path, "payload");
        var install = Path.Combine(temp.Path, "install");
        var backup = Path.Combine(temp.Path, "backup");
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(install);

        var executableName = OperatingSystem.IsWindows() ? "XcpNgCenter.Shell.exe" : "XcpNgCenter.Shell";
        File.WriteAllText(Path.Combine(payload, executableName), "new executable");
        File.WriteAllText(Path.Combine(payload, "XcpNgCenter.Shell.dll"), "new assembly");
        File.WriteAllText(Path.Combine(payload, "XcpNgCenter.Shell.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(payload, "data.txt"), "new data");
        File.WriteAllText(Path.Combine(install, "data.txt"), "old data");

        ShellUpdateInstaller.ApplyPayload(payload, install, backup);

        Assert.Equal("new data", File.ReadAllText(Path.Combine(install, "data.txt")));
        Assert.Equal("old data", File.ReadAllText(Path.Combine(backup, "data.txt")));
        Assert.Equal("new executable", File.ReadAllText(Path.Combine(install, executableName)));
    }

    [Fact]
    public void ApplyPayload_RejectsReservedStagingDirectory()
    {
        using var temp = new TemporaryDirectory();
        var payload = Path.Combine(temp.Path, "payload");
        var install = Path.Combine(temp.Path, "install");
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(install);
        CreateMinimumPayload(payload);
        Directory.CreateDirectory(Path.Combine(payload, ".xcpng-update"));
        File.WriteAllText(Path.Combine(payload, ".xcpng-update", "unexpected.txt"), "nope");

        Assert.Throws<InvalidDataException>(() =>
            ShellUpdateInstaller.ApplyPayload(payload, install, Path.Combine(temp.Path, "backup")));
    }

    [Fact]
    public void ApplyPayload_RejectsMismatchedAssemblyVersion()
    {
        using var temp = new TemporaryDirectory();
        var payload = Path.Combine(temp.Path, "payload");
        var install = Path.Combine(temp.Path, "install");
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(install);
        var executableName = OperatingSystem.IsWindows() ? "XcpNgCenter.Shell.exe" : "XcpNgCenter.Shell";
        File.WriteAllText(Path.Combine(payload, executableName), "new executable");
        File.Copy(typeof(ShellUpdateInstaller).Assembly.Location, Path.Combine(payload, "XcpNgCenter.Shell.dll"));
        File.WriteAllText(Path.Combine(payload, "XcpNgCenter.Shell.runtimeconfig.json"), "{}");

        Assert.Throws<InvalidDataException>(() =>
            ShellUpdateInstaller.ApplyPayload(
                payload,
                install,
                Path.Combine(temp.Path, "backup"),
                new Version(1, 2, 3, 4)));
    }

    private static void CreateZip(string path, string entryName, string content)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static void CreateTarGz(string path, TarEntry entry)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax);
        writer.WriteEntry(entry);
    }

    private static void CreateMinimumPayload(string payload)
    {
        var executableName = OperatingSystem.IsWindows() ? "XcpNgCenter.Shell.exe" : "XcpNgCenter.Shell";
        File.WriteAllText(Path.Combine(payload, executableName), "new executable");
        File.WriteAllText(Path.Combine(payload, "XcpNgCenter.Shell.dll"), "new assembly");
        File.WriteAllText(Path.Combine(payload, "XcpNgCenter.Shell.runtimeconfig.json"), "{}");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xcpng-update-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup only.
            }
        }
    }
}
