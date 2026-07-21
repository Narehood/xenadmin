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
    private readonly string _keyPath;

    public SavedServerStore(string? path = null)
    {
        var root = GetConfigRoot();
        _path = path ?? Path.Combine(root, "saved-servers.json");
        _keyPath = Path.Combine(root, "device.key");
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

    private static string GetConfigRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "XCP-ng", "XCP-ng Center Shell");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(xdg))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                home = Environment.GetEnvironmentVariable("HOME") ?? ".";
            xdg = Path.Combine(home, ".config");
        }

        return Path.Combine(xdg!, "XCP-ng", "XCP-ng Center Shell");
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
        var root = GetConfigRoot();
        Directory.CreateDirectory(root);
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
