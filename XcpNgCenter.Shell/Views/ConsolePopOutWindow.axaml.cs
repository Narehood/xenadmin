using System.ComponentModel;
using Avalonia.Controls;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class ConsolePopOutWindow : Window
{
    private MainViewModel? _main;

    public ConsolePopOutWindow()
    {
        InitializeComponent();
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
