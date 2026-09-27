using System.Text.Json;
using System.Text.Json.Serialization;

namespace XcpNgCenter.Shell.Services;

public enum ShellUpdateChannel { Stable, Beta }

/// <summary>
/// Persists the selected update channel and independent dismissal history for each channel.
/// </summary>
public sealed class ShellUpdatePreferences
{
    private readonly string _path;
    private readonly object _gate = new();

    public ShellUpdatePreferences(string? path = null)
    {
        _path = path ?? Path.Combine(ShellPaths.GetConfigRoot(ensureExists: false), "update-preferences.json");
    }

    public ShellUpdateChannel GetChannel()
    {
        lock (_gate) return ChannelOf(LoadUnlocked());
    }

    public void SetChannel(ShellUpdateChannel channel)
    {
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        lock (_gate)
        {
            var state = LoadUnlocked();
            state.UpdateChannel = (int)channel;
            SaveUnlocked(state);
        }
    }

    private static ShellUpdateChannel ChannelOf(State state) => state.UpdateChannel == (int)ShellUpdateChannel.Beta
        ? ShellUpdateChannel.Beta : ShellUpdateChannel.Stable;

    public string? GetDismissedVersion(ShellUpdateChannel? channel = null)
    {
        lock (_gate)
        {
            var state = LoadUnlocked();
            var dismissed = (channel ?? ChannelOf(state)) == ShellUpdateChannel.Beta
                ? state.DismissedBetaUpdateVersion : state.DismissedUpdateVersion;
            return string.IsNullOrWhiteSpace(dismissed) ? null : dismissed;
        }
    }

    public void SetDismissedVersion(string version, ShellUpdateChannel? channel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        lock (_gate)
        {
            var state = LoadUnlocked();
            if ((channel ?? ChannelOf(state)) == ShellUpdateChannel.Beta)
                state.DismissedBetaUpdateVersion = version.Trim();
            else
                state.DismissedUpdateVersion = version.Trim();
            SaveUnlocked(state);
        }
    }

    public void ClearDismissedVersion(ShellUpdateChannel? channel = null)
    {
        lock (_gate)
        {
            var state = LoadUnlocked();
            if ((channel ?? ChannelOf(state)) == ShellUpdateChannel.Beta)
                state.DismissedBetaUpdateVersion = null;
            else
                state.DismissedUpdateVersion = null;
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
        var fullPath = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class State
    {
        [JsonPropertyName("updateChannel")]
        public int UpdateChannel { get; set; }

        [JsonPropertyName("dismissedBetaUpdateVersion")]
        public string? DismissedBetaUpdateVersion { get; set; }

        [JsonPropertyName("dismissedUpdateVersion")]
        public string? DismissedUpdateVersion { get; set; }
    }
}
