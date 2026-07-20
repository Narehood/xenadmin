using Avalonia.Controls;
using Avalonia.Interactivity;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class AddServerWindow : Window
{
    public AddServerWindow()
    {
        InitializeComponent();
    }

    public AddServerWindow(MainViewModel main) : this()
    {
        DataContext = new AddServerViewModel(main, Close);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
