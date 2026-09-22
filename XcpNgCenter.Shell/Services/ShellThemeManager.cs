using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace XcpNgCenter.Shell.Services;

/// <summary>Applies saved appearance to every open shell window on the UI thread.</summary>
public sealed class ShellThemeManager : IDisposable
{
    private readonly Application _app;
    private readonly ShellAppSettings _settings;
    private (bool Light, string Accent)? _applied;
    private bool _disposed;

    public ShellThemeManager(Application app, ShellAppSettings settings)
    {
        _app = app;
        _settings = settings;
        _app.ActualThemeVariantChanged += OnThemeChanged;
        _settings.Changed += OnSettingsChanged;
        Apply();
    }

    private void OnThemeChanged(object? sender, EventArgs args) => Apply();

    private void OnSettingsChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void Apply()
    {
        if (_disposed)
            return;
        _app.RequestedThemeVariant = _settings.ThemeMode switch
        {
            ShellThemeMode.Light => ThemeVariant.Light,
            ShellThemeMode.System => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
        var palette = (Light: _app.ActualThemeVariant == ThemeVariant.Light, Accent: _settings.AccentColor);
        if (_applied == palette)
            return;
        _applied = palette;
        foreach (var (key, color) in ShellAppearance.CreatePalette(palette.Light, palette.Accent))
        {
            _app.Resources[key] = color;
            _app.Resources["Brush." + key] = new SolidColorBrush(color);
        }
        foreach (var theme in _app.Styles.OfType<FluentTheme>())
        {
            foreach (var (variant, colors) in theme.Palettes)
                colors.Accent = ShellAppearance.CreatePalette(variant == ThemeVariant.Light, palette.Accent)["AccentText"];
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _app.ActualThemeVariantChanged -= OnThemeChanged;
    }
}
