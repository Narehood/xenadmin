using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XenCenterLib;
using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class MainPasswordVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shell-vault-tests-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "app-settings.json");
    private string ServersPath => Path.Combine(_root, "saved-servers.json");

    public MainPasswordVaultTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void MetadataCannotDecryptAndCredentialsAreAuthenticated()
    {
        using var protection = MainPasswordProtection.Create("synthetic main password");
        var encrypted = protection.Protect("synthetic server secret");
        Assert.Equal("synthetic server secret", protection.Unprotect(encrypted));
        Assert.NotEqual(encrypted, protection.Protect("synthetic server secret"));
        using var samePassword = MainPasswordProtection.Create("synthetic main password");
        Assert.NotEqual(protection.Metadata.Salt, samePassword.Metadata.Salt);
        Assert.Null(MainPasswordProtection.Unlock("wrong", protection.Metadata));

        // Reproduce the previous attack: a persisted verifier must not be an AES key.
        var payload = Convert.FromBase64String(encrypted[4..]);
        using var aes = new AesGcm(Convert.FromBase64String(protection.Metadata.Verifier), 16);
        Assert.ThrowsAny<CryptographicException>(() => aes.Decrypt(payload.AsSpan(0, 12),
            payload.AsSpan(28), payload.AsSpan(12, 16), new byte[payload.Length - 28],
            Encoding.UTF8.GetBytes("XcpNgCenter saved password mp2")));
        foreach (var index in new[] { 0, 12, 28 })
        {
            var modified = payload.ToArray();
            modified[index] ^= 1;
            Assert.ThrowsAny<CryptographicException>(() => protection.Unprotect("mp2:" + Convert.ToBase64String(modified)));
        }
        protection.Dispose();
        Assert.Throws<ObjectDisposedException>(() => protection.Unprotect(encrypted));
    }

    [Fact]
    public void EnableMigratesDeviceCredentialsTogetherWithMetadata()
    {
        var store = new SavedServerStore(ServersPath);
        store.Save([new SavedServerEntry("first", "root", store.ProtectDevicePassword("first secret")),
            new SavedServerEntry("second", "root", store.ProtectDevicePassword("second secret")),
            new SavedServerEntry("no password", "root")]);
        using var vault = new MainPasswordVault(store, new ShellAppSettings(SettingsPath));
        Assert.False(vault.RequiresMainPassword);
        vault.ChangePassword("synthetic main password");
        Assert.True(vault.RequiresMainPassword);
        Assert.NotNull(store.ReadDocument()!.MainPassword);
        Assert.Equal(new[] { "first secret", "second secret", null }, store.Load().Select(e => vault.Unprotect(e.EncryptedPassword)));
        vault.Dispose();
        Assert.Null(vault.Unprotect(store.Load().First().EncryptedPassword));
    }

    [Fact]
    public void LegacyUnlockMigratesAtomicallyAndRestartNeedsPassword()
    {
        WriteLegacy();
        var store = new SavedServerStore(ServersPath);
        using (var vault = new MainPasswordVault(store, new ShellAppSettings(SettingsPath)))
        {
            var original = File.ReadAllText(ServersPath);
            Assert.False(vault.Unlock("wrong"));
            Assert.Equal(original, File.ReadAllText(ServersPath));
            Assert.True(vault.Unlock("old main"));
            Assert.Equal("server secret", vault.Unprotect(store.Load().Single().EncryptedPassword));
            Assert.StartsWith("mp2:", store.Load().Single().EncryptedPassword);
            Assert.DoesNotContain("mainPasswordHash", File.ReadAllText(SettingsPath));
            Assert.DoesNotContain(Convert.ToBase64String(EncryptionUtils.ComputeHash("old main")), File.ReadAllText(ServersPath));
        }
        using var restarted = new MainPasswordVault(store, new ShellAppSettings(SettingsPath));
        Assert.True(restarted.RequiresMainPassword);
        Assert.Null(restarted.Unprotect(store.Load().Single().EncryptedPassword));
        Assert.True(restarted.Unlock("old main"));
        Assert.Equal("server secret", restarted.Unprotect(store.Load().Single().EncryptedPassword));
    }

    [Fact]
    public void ChangeAndDisablePreserveCredentialsAndRejectPreviousPassword()
    {
        WriteLegacy();
        var store = new SavedServerStore(ServersPath);
        using var vault = new MainPasswordVault(store, new ShellAppSettings(SettingsPath));
        Assert.True(vault.Unlock("old main"));
        var stale = store.Load().ToList();
        vault.ChangePassword("new main");
        Assert.False(vault.VerifyPassword("old main"));
        Assert.True(vault.VerifyPassword("new main"));
        Assert.Equal("server secret", vault.Unprotect(store.Load().Single().EncryptedPassword));
        Assert.ThrowsAny<CryptographicException>(() => vault.Save(stale));
        vault.ChangePassword(null);
        Assert.False(vault.RequiresMainPassword);
        Assert.Equal("server secret", store.UnprotectDevicePassword(store.Load().Single().EncryptedPassword));
        using var restarted = new MainPasswordVault(store, new ShellAppSettings(SettingsPath));
        Assert.False(restarted.RequiresMainPassword);
    }

    [Fact]
    public void CancelledAndUndecryptableMigrationsLeaveBothFilesIntact()
    {
        WriteLegacy();
        var originalSettings = File.ReadAllText(SettingsPath);
        var originalServers = File.ReadAllText(ServersPath);
        var store = new SavedServerStore(ServersPath);
        using var vault = new MainPasswordVault(store, new ShellAppSettings(SettingsPath));
        Assert.Throws<OperationCanceledException>(() => vault.Unlock("old main", new CancellationToken(true)));
        Assert.Equal(originalServers, File.ReadAllText(ServersPath));
        Assert.Equal(originalSettings, File.ReadAllText(SettingsPath));
        Assert.False(vault.IsUnlocked);
        store.Save(store.Load().Append(new SavedServerEntry("broken", "root", "mp1:invalid")));
        originalServers = File.ReadAllText(ServersPath);
        Assert.Throws<CryptographicException>(() => vault.Unlock("old main"));
        Assert.Equal(originalServers, File.ReadAllText(ServersPath));
        Assert.Equal(originalSettings, File.ReadAllText(SettingsPath));
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task ConcurrentPasswordChangesHaveOneCommitAndARecoverableVault()
    {
        WriteLegacy();
        var store = new SavedServerStore(ServersPath);
        using var first = new MainPasswordVault(store, new ShellAppSettings(SettingsPath));
        Assert.True(first.Unlock("old main"));
        using var second = new MainPasswordVault(new SavedServerStore(ServersPath), new ShellAppSettings(SettingsPath));
        Assert.True(second.Unlock("old main"));
        using var start = new ManualResetEventSlim();
        Task<bool> Change(MainPasswordVault vault, string password) => Task.Run(() =>
        {
            start.Wait();
            try { vault.ChangePassword(password); return true; }
            catch (InvalidOperationException) { return false; }
        });
        var a = Change(first, "first replacement");
        var b = Change(second, "second replacement");
        start.Set();
        var results = await Task.WhenAll(a, b);
        Assert.Single(results, success => success);
        using var restarted = new MainPasswordVault(store, new ShellAppSettings(SettingsPath));
        Assert.True(restarted.Unlock(results[0] ? "first replacement" : "second replacement"));
        Assert.Equal("server secret", restarted.Unprotect(store.Load().Single().EncryptedPassword));
    }

    [Fact]
    public void AnotherUnlockedInstanceCannotOverwriteRotatedCredentials()
    {
        WriteLegacy();
        var store = new SavedServerStore(ServersPath);
        using var first = new MainPasswordVault(store, new ShellAppSettings(SettingsPath));
        Assert.True(first.Unlock("old main"));
        using var second = new MainPasswordVault(new SavedServerStore(ServersPath), new ShellAppSettings(SettingsPath));
        Assert.True(second.Unlock("old main"));
        var previousEntries = store.Load().ToList();
        first.ChangePassword("new main");
        var committed = File.ReadAllText(ServersPath);
        Assert.Throws<InvalidOperationException>(() => second.Save(previousEntries));
        Assert.Throws<InvalidOperationException>(() => second.ChangePassword("third main"));
        Assert.Throws<InvalidOperationException>(() => second.Unlock("old main"));
        Assert.Equal(committed, File.ReadAllText(ServersPath));
        Assert.Equal("server secret", first.Unprotect(store.Load().Single().EncryptedPassword));
    }

    [Fact]
    public void CancellationAtFileCommitKeepsPreviousDocument()
    {
        WriteLegacy();
        var before = File.ReadAllText(ServersPath);
        var store = new SavedServerStore(ServersPath);
        Assert.Throws<OperationCanceledException>(() => store.Commit(
            new SavedServerStore.Document(2, null, []), new CancellationToken(true)));
        Assert.Equal(before, File.ReadAllText(ServersPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void SettingsCleanupFailureDoesNotRollbackCommittedVault()
    {
        if (!OperatingSystem.IsWindows())
            return;
        WriteLegacy();
        var store = new SavedServerStore(ServersPath);
        using (var vault = new MainPasswordVault(store, new ShellAppSettings(SettingsPath)))
        {
            using (var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.True(vault.Unlock("old main"));
                Assert.NotNull(vault.CleanupWarning);
                Assert.StartsWith("mp2:", store.Load().Single().EncryptedPassword);
                Assert.Equal("server secret", vault.Unprotect(store.Load().Single().EncryptedPassword));
            }
        }
        using var restarted = new MainPasswordVault(store, new ShellAppSettings(SettingsPath));
        Assert.True(restarted.Unlock("old main"));
        Assert.DoesNotContain("mainPasswordHash", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void CommitFailureKeepsOldPasswordAndCleansTemporaryFile()
    {
        WriteLegacy();
        var store = new SavedServerStore(ServersPath);
        using var vault = new MainPasswordVault(store, new ShellAppSettings(SettingsPath));
        Assert.True(vault.Unlock("old main"));
        var before = File.ReadAllText(ServersPath);
        // Windows denies replacing an open file without FileShare.Delete.
        if (OperatingSystem.IsWindows())
        {
            using (var locked = new FileStream(ServersPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Record.Exception(() => vault.ChangePassword("new main"));
                Assert.True(error is IOException or UnauthorizedAccessException,
                    $"Expected a file replacement failure, got {error?.GetType().FullName ?? "no exception"}.");
            }
            Assert.Equal(before, File.ReadAllText(ServersPath));
            Assert.True(vault.VerifyPassword("old main"));
            Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedVaultOverridesStaleLegacySettingsAfterCrash(bool disable)
    {
        WriteLegacy();
        var legacySettings = File.ReadAllText(SettingsPath);
        var store = new SavedServerStore(ServersPath);
        using (var vault = new MainPasswordVault(store, new ShellAppSettings(SettingsPath)))
        {
            Assert.True(vault.Unlock("old main"));
            if (disable)
                vault.ChangePassword(null);
        }
        File.WriteAllText(SettingsPath, legacySettings); // interrupted cleanup
        using var restarted = new MainPasswordVault(store, new ShellAppSettings(SettingsPath));
        Assert.Equal(!disable, restarted.RequiresMainPassword);
        if (!disable)
            Assert.True(restarted.Unlock("old main"));
        Assert.Equal("server secret", restarted.Unprotect(store.Load().Single().EncryptedPassword));
        Assert.DoesNotContain("mainPasswordHash", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void CorruptVaultCannotBeSilentlyOverwritten()
    {
        File.WriteAllText(ServersPath, "{ invalid");
        using var vault = new MainPasswordVault(new SavedServerStore(ServersPath), new ShellAppSettings(SettingsPath));
        Assert.True(vault.RequiresMainPassword);
        Assert.Throws<InvalidDataException>(() => vault.ChangePassword("new main"));
        Assert.Throws<InvalidDataException>(() => vault.Save([]));
        Assert.Equal("{ invalid", File.ReadAllText(ServersPath));
    }

    private void WriteLegacy()
    {
        var hash = EncryptionUtils.ComputeHash("old main");
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new
            { requireMainPassword = true, mainPasswordHash = Convert.ToBase64String(hash) }));
        File.WriteAllText(ServersPath, JsonSerializer.Serialize(new[]
            { new SavedServerEntry("synthetic.local", "root", "mp1:" + EncryptionUtils.EncryptString("server secret", hash)) }));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
