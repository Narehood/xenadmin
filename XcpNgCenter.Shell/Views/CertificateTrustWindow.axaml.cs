using Avalonia.Controls;
using Avalonia.Interactivity;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.Views;

public partial class CertificateTrustWindow : Window
{
    public CertificateTrustWindow()
    {
        InitializeComponent();
    }

    public CertificateTrustWindow(CertificateTrustRequest request) : this()
    {
        DataContext = request;
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
