using Avalonia.Media;
using XcpNgCenter.Shell.Controls;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class AppearanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "appearance-tests-" + Guid.NewGuid());
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"themeMode\":99,\"accentColor\":\"invalid\"}")]
    [InlineData("{\"themeMode\":-1,\"accentColor\":null}")]
    [InlineData("{\"accentColor\":\"#00112233\"}")]
    public void OlderOrInvalidAppearanceUsesOriginalDefaults(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);
        using var settings = new ShellAppSettings(SettingsPath);
        Assert.Equal(ShellThemeMode.Dark, settings.ThemeMode);
        Assert.Equal(ShellAppearance.DefaultAccent, settings.AccentColor);
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData(ShellThemeMode.Dark)]
    [InlineData(ShellThemeMode.Light)]
    [InlineData(ShellThemeMode.System)]
    public void AppearanceSurvivesRestartAndPreservesOtherPreferences(ShellThemeMode mode)
    {
        using (var settings = new ShellAppSettings(SettingsPath))
        {
            settings.HideVmNames = true;
            settings.ConnectionTimeoutSeconds = 47;
            _ = new AppearanceViewModel(settings)
            {
                SelectedTheme = mode.ToString(),
                AccentColor = Color.Parse("#365abc")
            };
        }

        using (var reloaded = new ShellAppSettings(SettingsPath))
        {
            var reopened = new AppearanceViewModel(reloaded);
            Assert.Equal(mode, reloaded.ThemeMode);
            Assert.Equal("#365ABC", reopened.AccentHex);
            Assert.Equal(mode.ToString(), reopened.SelectedTheme);
            Assert.True(reloaded.HideVmNames);
            Assert.Equal(47, reloaded.ConnectionTimeoutSeconds);
            reopened.ResetAppearanceCommand.Execute(null);
        }

        using var reset = new ShellAppSettings(SettingsPath);
        Assert.Equal(ShellThemeMode.Dark, reset.ThemeMode);
        Assert.Equal(ShellAppearance.DefaultAccent, reset.AccentColor);
        Assert.True(reset.HideVmNames);
        Assert.Equal(47, reset.ConnectionTimeoutSeconds);
    }

    [Fact]
    public void OpeningAppearanceDoesNotWriteSettings()
    {
        using var settings = new ShellAppSettings(SettingsPath);
        var changes = 0;
        settings.Changed += () => changes++;
        _ = new AppearanceViewModel(settings);
        Assert.Equal(0, changes);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void AccentNormalizesAndIgnoresTransparencyWithoutRedundantSaves()
    {
        using var settings = new ShellAppSettings(SettingsPath);
        var changes = 0;
        settings.Changed += () => changes++;
        settings.AccentColor = "#365abc";
        settings.AccentColor = "#365ABC";
        Assert.Equal(1, changes);
        var viewModel = new AppearanceViewModel(settings) { AccentColor = Color.FromArgb(0, 0x22, 0x44, 0x66) };
        settings.Dispose();
        using var reloaded = new ShellAppSettings(SettingsPath);
        Assert.Equal("#224466", reloaded.AccentColor);
        Assert.Equal("#224466", viewModel.AccentHex);
    }

    [Fact]
    public async Task AccentPreviewIsImmediateButPersistenceIsDebounced()
    {
        using var settings = new ShellAppSettings(SettingsPath) { ThemeMode = ShellThemeMode.Light };
        var persistedBeforeDrag = File.ReadAllText(SettingsPath);
        var changes = 0;
        settings.Changed += () => changes++;

        for (var component = 0; component < 100; component++)
            settings.AccentColor = $"#{component:X2}5AA5";

        Assert.Equal(100, changes);
        Assert.Equal("#635AA5", settings.AccentColor);
        Assert.Equal(persistedBeforeDrag, File.ReadAllText(SettingsPath));

        await WaitForAsync(() =>
        {
            using var reloaded = new ShellAppSettings(SettingsPath);
            return reloaded.AccentColor == "#635AA5";
        });
    }

    [Fact]
    public void DisposeFlushesTheLastAccentImmediately()
    {
        var settings = new ShellAppSettings(SettingsPath);
        settings.AccentColor = "#365ABC";
        Assert.False(File.Exists(SettingsPath));

        settings.Dispose();

        using var reloaded = new ShellAppSettings(SettingsPath);
        Assert.Equal("#365ABC", reloaded.AccentColor);
    }

    [Theory]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    [InlineData("#F07318")]
    [InlineData("#777777")]
    [InlineData("#FFFF00")]
    [InlineData("#0000FF")]
    [InlineData("#00FF00")]
    [InlineData("#FF0000")]
    public void CustomAccentsKeepLabelsReadableInBothThemes(string accent)
    {
        foreach (var light in new[] { false, true })
        {
            var palette = ShellAppearance.CreatePalette(light, accent);
            Assert.Equal(Color.Parse(accent), palette["BrandOrange"]);
            AssertContrast(palette["AccentForeground"], palette["BrandOrange"]);
            AssertContrast(palette["AccentForeground"], palette["BrandOrangeDeep"]);
            foreach (var surface in new[] { "BgDeep", "BgPanel", "BgElevated" })
            {
                AssertContrast(palette["AccentText"], palette[surface]);
                AssertContrast(palette["TextPrimary"], palette[surface]);
                AssertContrast(palette["TextMuted"], palette[surface]);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChartSeriesStayReadableOnThePlot(bool light)
    {
        var plot = ShellAppearance.CreatePalette(light, ShellAppearance.DefaultAccent)["BgElevated"];
        foreach (var color in new[]
        {
            "#F07318", "#3DBE7A", "#5B9BD5", "#E35D5D", "#C9A227",
            "#9B7EDE", "#4ECDC4", "#FF8FAB", "#9AA6B2"
        })
        {
            var ink = PerformanceChart.ReadableSeriesColor(color, plot);
            AssertContrast(ink, plot);
            if (light)
                Assert.NotEqual(Color.Parse(color), ink);
            else if (color is "#F07318" or "#9AA6B2")
                Assert.Equal(Color.Parse(color), ink);
        }
    }

    private static void AssertContrast(Color foreground, Color background)
    {
        var a = ShellAppearance.Luminance(foreground);
        var b = ShellAppearance.Luminance(background);
        var ratio = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        Assert.True(ratio >= 4.5, $"{foreground} on {background}: {ratio:F2}:1");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for debounced appearance persistence.");
            await Task.Delay(25);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
