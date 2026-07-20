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
/// Persists shell server list (address + username + optional DPAPI-protected password).
/// </summary>
public sealed class SavedServerStore
{
    private readonly string _path;

    public SavedServerStore(string? path = null)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XCP-ng",
            "XCP-ng Center Shell");
        Directory.CreateDirectory(root);
        _path = path ?? Path.Combine(root, "saved-servers.json");
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
        if (string.IsNullOrEmpty(password))
            return null;

        try
        {
            // Windows DPAPI via XenCenterLib; no-op/fail closed on unsupported platforms.
            return EncryptionUtils.Protect(password);
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
            return EncryptionUtils.Unprotect(encrypted);
        }
        catch
        {
            return null;
        }
    }
}
