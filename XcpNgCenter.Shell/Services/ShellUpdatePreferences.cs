using System.Text.Json;
using System.Text.Json.Serialization;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Remembers which GitHub release version the user dismissed so we do not re-nag.
/// </summary>
public sealed class ShellUpdatePreferences
{
    private readonly string _path;
    private readonly object _gate = new();

    public ShellUpdatePreferences(string? path = null)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XCP-ng",
            "XCP-ng Center Shell");
        Directory.CreateDirectory(root);
        _path = path ?? Path.Combine(root, "update-preferences.json");
    }

    public string? GetDismissedVersion()
    {
        lock (_gate)
        {
            var state = LoadUnlocked();
            return string.IsNullOrWhiteSpace(state.DismissedUpdateVersion)
                ? null
                : state.DismissedUpdateVersion;
        }
    }

    public void SetDismissedVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        lock (_gate)
        {
            var state = LoadUnlocked();
            state.DismissedUpdateVersion = version.Trim();
            SaveUnlocked(state);
        }
    }

    private State LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_path))
                return new State();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<State>(json) ?? new State();
        }
        catch
        {
            return new State();
        }
    }

    private void SaveUnlocked(State state)
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }

    private sealed class State
    {
        [JsonPropertyName("dismissedUpdateVersion")]
        public string? DismissedUpdateVersion { get; set; }
    }
}
