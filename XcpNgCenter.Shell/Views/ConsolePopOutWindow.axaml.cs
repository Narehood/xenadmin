using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class ConsolePopOutWindow : Window
{
    private MainViewModel? _main;
    private WindowState _windowStateBeforeFullScreen = WindowState.Normal;

    public ConsolePopOutWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnConsoleShortcutKeyDown, RoutingStrategies.Tunnel);
    }

    public ConsolePopOutWindow(MainViewModel main) : this()
    {
        _main = main;
        DataContext = main;
        UpdateTitle();
        main.PropertyChanged += OnMainPropertyChanged;
        Closed += (_, _) =>
        {
            if (_main != null)
                _main.PropertyChanged -= OnMainPropertyChanged;
        };
    }

    public void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _windowStateBeforeFullScreen == WindowState.FullScreen
                ? WindowState.Normal
                : _windowStateBeforeFullScreen;
            return;
        }

        _windowStateBeforeFullScreen = WindowState;
        WindowState = WindowState.FullScreen;
    }

    private void OnConsoleShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (_main == null)
            return;

        if (ConsoleShortcutMatcher.Matches(e, _main.ConsoleFullscreenShortcut))
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        if (ConsoleShortcutMatcher.Matches(e, _main.ConsoleDockShortcut))
        {
            _main.ReattachConsoleCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedInfraNode) or nameof(MainViewModel.BrandName))
            UpdateTitle();
    }

    private void UpdateTitle()
    {
        if (_main == null)
            return;
        Title = $"Console — {_main.SelectedInfraNode?.Title ?? _main.BrandName}";
    }
}
