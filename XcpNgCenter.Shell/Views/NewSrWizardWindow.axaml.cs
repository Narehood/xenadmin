using Avalonia.Controls;
using Avalonia.Interactivity;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class NewSrWizardWindow : Window
{
    public NewSrWizardWindow()
    {
        InitializeComponent();
    }

    public NewSrWizardWindow(IXenConnection connection, Host host) : this()
    {
        DataContext = new NewSrWizardViewModel(connection, host, Close);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
