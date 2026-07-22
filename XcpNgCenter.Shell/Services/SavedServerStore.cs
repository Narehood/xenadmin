using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XenCenterLib;

namespace XcpNgCenter.Shell.Services;

public sealed record SavedServerEntry(
    string Address,
    string Username,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EncryptedPassword = null)
{
    [JsonIgnore]
    public bool HasSavedPassword => !string.IsNullOrWhiteSpace(EncryptedPassword);

    [JsonIgnore]
    public string DisplayAddress => IdentifierPrivacy.Address(Address);

    [JsonIgnore]
    public string Subtitle => HasSavedPassword
        ? $"{Username} · password saved"
        : Username;
}

/// <summary>
/// Persists shell server list (address + username + optional protected password).
/// Windows uses DPAPI; other platforms use a per-user AES key file under the config root.
/// </summary>
public sealed class SavedServerStore
{
    /// <summary>
    /// True when the OS can protect passwords for the current user
    /// (Windows DPAPI, or a Unix user-only AES device key).
    /// </summary>
    public static bool CanPersistPasswords =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private readonly string _path;

    public SavedServerStore(string? path = null)
    {
        // Defer directory creation to Save / device-key persistence.
        _path = path ?? Path.Combine(ShellPaths.GetConfigRoot(ensureExists: false), "saved-servers.json");
    }

    public IReadOnlyList<SavedServerEntry> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return Array.Empty<SavedServerEntry>();

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<List<SavedServerEntry>>(json);
            if (loaded == null)
                return Array.Empty<SavedServerEntry>();

            return loaded
                .Where(e => !string.IsNullOrWhiteSpace(e.Address))
                .GroupBy(e => e.Address.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .ToList();
        }
        catch
        {
            return Array.Empty<SavedServerEntry>();
        }
    }

    public void Save(IEnumerable<SavedServerEntry> entries)
    {
        try
        {
            var list = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Address))
                .GroupBy(e => e.Address.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var last = g.Last();
                    return new SavedServerEntry(
                        last.Address.Trim(),
                        last.Username?.Trim() ?? string.Empty,
                        string.IsNullOrWhiteSpace(last.EncryptedPassword) ? null : last.EncryptedPassword);
                })
                .ToList();

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Persistence failures should not break the shell.
        }
    }

    public static string? ProtectPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || !CanPersistPasswords)
            return null;

        try
        {
            if (OperatingSystem.IsWindows())
                return EncryptionUtils.Protect(password);

            return ProtectWithDeviceKey(password);
        }
        catch
        {
            return null;
        }
    }

    public static string? UnprotectPassword(string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted))
            return null;

        try
        {
            if (IsMainPasswordProtected(encrypted))
                return null; // Requires UnprotectPasswordWithMainPassword.

            if (OperatingSystem.IsWindows())
                return EncryptionUtils.Unprotect(encrypted);

            return UnprotectWithDeviceKey(encrypted);
        }
        catch
        {
            // Legacy / corrupt blobs fail closed.
            return null;
        }
    }

    private const string MainPasswordPrefix = "mp1:";

    public static bool IsMainPasswordProtected(string? encrypted)
        => !string.IsNullOrWhiteSpace(encrypted)
           && encrypted.StartsWith(MainPasswordPrefix, StringComparison.Ordinal);

    /// <summary>Encrypt a server password with the session main-password hash (AES).</summary>
    public static string? ProtectPasswordWithMainPassword(string? password, byte[] mainPasswordHash)
    {
        if (string.IsNullOrEmpty(password) || mainPasswordHash.Length == 0)
            return null;

        try
        {
            return MainPasswordPrefix + EncryptionUtils.EncryptString(password, mainPasswordHash);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decrypt a main-password blob using the plaintext main password from unlock.</summary>
    public static string? UnprotectPasswordWithMainPassword(string? encrypted, string mainPasswordPlain)
    {
        if (!IsMainPasswordProtected(encrypted) || string.IsNullOrEmpty(mainPasswordPlain))
            return null;

        try
        {
            return EncryptionUtils.DecryptString(encrypted![MainPasswordPrefix.Length..], mainPasswordPlain);
        }
        catch
        {
            return null;
        }
    }

    private static string ProtectWithDeviceKey(string password)
    {
        var key = LoadOrCreateDeviceKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(password);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        // v1 | nonce | tag | cipher
        var payload = new byte[1 + nonce.Length + tag.Length + cipher.Length];
        payload[0] = 1;
        Buffer.BlockCopy(nonce, 0, payload, 1, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, 1 + nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, payload, 1 + nonce.Length + tag.Length, cipher.Length);
        return Convert.ToBase64String(payload);
    }

    private static string? UnprotectWithDeviceKey(string encrypted)
    {
        var payload = Convert.FromBase64String(encrypted);
        if (payload.Length < 1 + 12 + 16 + 1 || payload[0] != 1)
            return null;

        var key = LoadOrCreateDeviceKey();
        var nonce = payload.AsSpan(1, 12);
        var tag = payload.AsSpan(13, 16);
        var cipher = payload.AsSpan(29);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] LoadOrCreateDeviceKey()
    {
        var root = ShellPaths.GetConfigRoot(ensureExists: true);
        var keyPath = Path.Combine(root, "device.key");
        if (File.Exists(keyPath))
        {
            var existing = File.ReadAllBytes(keyPath);
            if (existing.Length == 32)
            {
                EnforceUserOnlyKeyPermissions(keyPath);
                return existing;
            }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(keyPath, key);
        EnforceUserOnlyKeyPermissions(keyPath);
        return key;
    }

    /// <summary>
    /// Requires user-read/user-write only on Unix. Fail closed on chmod or validation errors.
    /// </summary>
    private static void EnforceUserOnlyKeyPermissions(string keyPath)
    {
        if (OperatingSystem.IsWindows())
            return;

        const UnixFileMode userOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(keyPath, userOnly);
        var mode = File.GetUnixFileMode(keyPath);
        if ((mode & ~userOnly) != 0 || (mode & userOnly) != userOnly)
            throw new CryptographicException("device.key must be user-read/user-write only.");
    }
}
