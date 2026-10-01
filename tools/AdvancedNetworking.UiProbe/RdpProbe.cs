using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using XcpNgCenter.Shell.ViewModels;
using XcpNgCenter.Shell.Views;
using Console = System.Console;

sealed partial class ProbeApp
{
    void CheckRemoteDesktop(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        DispatcherTimer.RunOnce(async () =>
        {
            try
            {
                await CheckRdpDialog(lifetime);
                File.WriteAllLines(Path.Combine(Evidence, "results.log"), checks.Prepend(
                    "PASS: production RDP dialog; no external client, profile or credentials used."));
                Console.WriteLine($"Passed {checks.Count} Remote Desktop UI checks. Evidence: {Evidence}");
                theme?.Dispose(); settings?.Dispose(); lifetime.Shutdown(0);
            }
            catch (Exception error) { Fail(lifetime, error); }
        }, TimeSpan.FromMilliseconds(100));
    }

    async Task CheckRdpDialog(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        var window = new RdpConnectWindow("Synthetic guest", ["192.0.2.10", "2001:db8::10"]);
        lifetime.MainWindow = window;
        window.Position = new PixelPoint(-10000, -10000);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowActivated = false; window.ShowInTaskbar = false; window.Show();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        var model = (RdpConnectViewModel)window.DataContext!;
        var address = window.FindControl<TextBox>("AddressText")!;
        var port = window.FindControl<TextBox>("PortText")!;
        var button = window.FindControl<Button>("ConnectButton")!;
        Require(address.Text == "192.0.2.10" && port.Text == "3389", "RDP fields show the suggested guest address and default port");
        address.Text = "192.0.2.10 /cert:ignore";
        ClickCommandButton(button, model.ConnectCommand);
        window.UpdateLayout();
        Require(model.HasError && window.IsVisible, "RDP invalid input keeps the actual dialog open with an error");
        address.Text = "2001:db8::20";
        port.Text = "3390";
        Require(model.Address == address.Text && model.Port == port.Text && !model.HasError,
            "RDP manual address and port edits update the bound model without being cleared by suggestions");
        foreach (var scale in new[] { 1d, 1.5, 2d }) Render(window, "rdp", scale);
        Require(button.IsEffectivelyVisible && button.Bounds.Width > 0 && button.Bounds.Height > 0,
            "RDP action remains visible after an error and manual edits");
        ClickCommandButton(button, model.ConnectCommand);
        Require(!window.IsVisible, "RDP valid review closes the dialog without launching a client in the probe");
    }

}
