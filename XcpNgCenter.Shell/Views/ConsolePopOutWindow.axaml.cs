using Avalonia.Controls;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class ConsolePopOutWindow : Window
{
    public ConsolePopOutWindow()
    {
        InitializeComponent();
    }

    public ConsolePopOutWindow(MainViewModel main) : this()
    {
        DataContext = main;
        Title = $"Console — {main.SelectedInfraNode?.Title ?? main.BrandName}";
    }
}
