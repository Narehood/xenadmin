using System.Text.Json;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Persists hostname → certificate hash pins for shell TOFU (separate from WinForms KnownServers).
/// </summary>
public sealed class TofuCertificateStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, string> _pins = new(StringComparer.OrdinalIgnoreCase);

    public TofuCertificateStore(string? path = null)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XCP-ng",
            "XCP-ng Center Shell");
        Directory.CreateDirectory(root);
        _path = path ?? Path.Combine(root, "known-servers.json");
        Load();
    }

    public bool TryGet(string hostname, out string hash)
    {
        lock (_gate)
            return _pins.TryGetValue(hostname, out hash!);
    }

    public void Set(string hostname, string hash)
    {
        lock (_gate)
        {
            _pins[hostname] = hash;
            Save();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _pins.Count;
        }
    }

    public bool Remove(string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return false;

        lock (_gate)
        {
            if (!_pins.Remove(hostname.Trim()))
                return false;
            Save();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_pins.Count == 0)
                return;
            _pins.Clear();
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (loaded != null)
                _pins = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_pins, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}
