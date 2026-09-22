using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class AppearanceViewModel : ViewModelBase
{
    private readonly ShellAppSettings _settings;

    public AppearanceViewModel(ShellAppSettings settings)
    {
        _settings = settings;
        _selectedTheme = settings.ThemeMode.ToString();
        _accentColor = Color.Parse(settings.AccentColor);
    }

    public IReadOnlyList<string> Themes { get; } = ["Dark", "Light", "System"];

    [ObservableProperty]
    private string _selectedTheme;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccentHex))]
    private Color _accentColor;

    public string AccentHex => ShellAppearance.ToHex(AccentColor);

    partial void OnSelectedThemeChanged(string value)
    {
        if (Enum.TryParse<ShellThemeMode>(value, out var mode) && Enum.IsDefined(mode))
            _settings.ThemeMode = mode;
    }

    partial void OnAccentColorChanged(Color value) => _settings.AccentColor = ShellAppearance.ToHex(value);

    [RelayCommand]
    private void ResetAppearance()
    {
        SelectedTheme = "Dark";
        AccentColor = Color.Parse(ShellAppearance.DefaultAccent);
    }
}
