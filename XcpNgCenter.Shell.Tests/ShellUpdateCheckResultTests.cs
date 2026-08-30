using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ShellUpdateCheckResultTests
{
    [Fact]
    public void Preferences_ClearDismissedVersion_RemovesStoredValue()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "update-preferences.json");
        var prefs = new ShellUpdatePreferences(path);
        prefs.SetDismissedVersion("2026.8.30.1");
        Assert.Equal("2026.8.30.1", prefs.GetDismissedVersion());

        prefs.ClearDismissedVersion();
        Assert.Null(prefs.GetDismissedVersion());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xcpng-update-prefs-{Guid.NewGuid():N}");
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
