using System.Text.Json;

namespace XcpNgCenter.Shell.Services;

public sealed record SavedServerEntry(string Address, string Username);

/// <summary>
/// Persists shell server list (address + username only; passwords are not stored).
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
                    return new SavedServerEntry(last.Address.Trim(), last.Username?.Trim() ?? string.Empty);
                })
                .ToList();

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Preview: persistence failures should not break the shell.
        }
    }
}
