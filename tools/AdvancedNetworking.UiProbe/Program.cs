using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XcpNgCenter.Shell;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using XcpNgCenter.Shell.Views;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using Console = System.Console;
using Task = System.Threading.Tasks.Task;

sealed class ProbeApp : App
{
    public static bool ConnectionSettingsOnly { get; set; }
    static readonly string Evidence = Path.GetDirectoryName(typeof(ProbeApp).Assembly.Location)!;
    readonly List<string> checks = [];
    ShellAppSettings? settings;
    ShellThemeManager? theme;

    public override void Initialize()
    {
        // Load the production application styles while bypassing profile/bootstrap initialization.
        // Avalonia compiles App.Initialize's Load call into this resource-only entry point.
        typeof(App).GetMethod("!XamlIlPopulateTrampoline", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [this]);
        settings = new ShellAppSettings(Path.Combine(Evidence, "synthetic-settings.json"));
        theme = new ShellThemeManager(this, settings);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            if (ConnectionSettingsOnly)
            {
                CheckConnectionSettings(lifetime);
                return;
            }
            var conn = new XenConnection();
            T Add<T>(string reference, T value) where T : XenObject<T>
            { conn.Cache.UpdateFrom(conn, [new ObjectChange(typeof(T), reference, value)]); return conn.Resolve(new XenRef<T>(reference)); }
            Add("pool", new Pool { master = new("host0") });
            var hosts = Enumerable.Range(0, 2).Select(i =>
            {
                Add("health" + i, new Host_metrics { live = true });
                return Add("host" + i, new Host { name_label = "Synthetic host " + i, uuid = "host-uuid" + i,
                    metrics = new("health" + i), enabled = true,
                    software_version = new() { ["network_backend"] = "openvswitch", ["product_version"] = "8.3", ["platform_version"] = "3.0.0" },
                    license_params = new() { ["restrict_network_sriov"] = "false" } });
            }).ToArray();
            foreach (var device in Enumerable.Range(0, 4))
            {
                var network = Add("network" + device, new XenAPI.Network { name_label = device == 0 ? "Management" : "Physical NIC " + device,
                    uuid = "network-uuid" + device, MTU = 1500 });
                foreach (var index in Enumerable.Range(0, 2))
                {
                    Add($"metrics{index}-{device}", new PIF_metrics { vendor_id = "8086", device_id = "1572", carrier = true, speed = 1000 });
                    var pif = Add($"pif{index}-{device}", new PIF { device = "eth" + device, uuid = $"pif-uuid{index}-{device}",
                        host = new(hosts[index].opaque_ref), network = new(network.opaque_ref), metrics = new($"metrics{index}-{device}"),
                        physical = true, managed = true, currently_attached = true, VLAN = -1, MTU = 1500,
                        MAC = $"02:00:00:00:0{index}:0{device}", management = device == 0,
                        ip_configuration_mode = device == 0 ? ip_configuration_mode.Static : ip_configuration_mode.None,
                        IP = device == 0 ? $"192.0.2.{index + 10}" : "", netmask = device == 0 ? "255.255.255.0" : "",
                        capabilities = device == 3 ? ["sriov"] : [] });
                    hosts[index].PIFs = [.. hosts[index].PIFs, new(pif.opaque_ref)];
                    network.PIFs = [.. network.PIFs, new(pif.opaque_ref)];
                }
            }
            var bond = new BondEditorWindow(conn, null, _ => { });
            var ip = new HostIpEditorWindow(conn, hosts[0], _ => { });
            var sriov = new SriovNetworkWindow(conn, _ => { });
            var windows = new Window[] { bond, ip, sriov };
            lifetime.MainWindow = bond;
            foreach (var window in windows)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = new PixelPoint(-10000, -10000);
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Show();
            }
            DispatcherTimer.RunOnce(async () =>
            {
                try
                {
                    var bvm = (BondEditorViewModel)bond.DataContext!;
                    var bchecks = bond.GetVisualDescendants().OfType<CheckBox>().ToList();
                    bchecks.Single(b => b.Content?.ToString()?.StartsWith("eth1") == true).IsChecked = true;
                    bchecks.Single(b => b.Content?.ToString()?.StartsWith("eth2") == true).IsChecked = true;
                    Require(bvm.Members.Count(m => m.IsSelected) == 2, "Bond checkbox selections update the reviewed member list");
                    Require(!bchecks.Single(b => b.Content?.ToString()?.StartsWith("eth0") == true).IsEffectivelyEnabled, "Management NIC cannot be selected for bonding");
                    bond.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem = BondManagement.Modes[2];
                    Require(bvm.IsLacp && bvm.SelectedMode == BondManagement.Modes[2], "Bond mode selector updates LACP state");
                    bond.GetVisualDescendants().OfType<TextBox>().First().Text = "Reviewed synthetic bond";
                    Require(bvm.NameLabel == "Reviewed synthetic bond", "Bond name textbox updates draft");

                    var ivm = (HostIpEditorViewModel)ip.DataContext!;
                    var icombos = ip.GetVisualDescendants().OfType<ComboBox>().ToList();
                    icombos.Single(c => ReferenceEquals(c.ItemsSource, ivm.Families)).SelectedItem = HostIpFamily.IPv6;
                    Require(ivm.Family == HostIpFamily.IPv6 && !ivm.IsIpv4, "Host IP family selector switches labels and IPv4-only controls");
                    icombos.Last().SelectedItem = HostIpMode.Static;
                    Require(ivm.Mode == HostIpMode.Static && ivm.IsStatic, "Host IP assignment selector updates static editor state");
                    ip.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible).Text = "2001:db8::10/64";
                    Require(ivm.Request.Address == "2001:db8::10/64", "Host IPv6 address textbox updates request");

                    var svm = (SriovNetworkViewModel)sriov.DataContext!;
                    var scombo = sriov.GetVisualDescendants().OfType<ComboBox>().Single();
                    Require(ReferenceEquals(scombo.SelectedItem, svm.SelectedUplink) && svm.SelectedUplink?.Device == "eth3" && svm.SelectedUplink.Error == null,
                        "SR-IOV selected NIC binding retains complete supported pool target");
                    sriov.GetVisualDescendants().OfType<TextBox>().First().Text = "Reviewed synthetic SR-IOV";
                    Require(svm.NameLabel == "Reviewed synthetic SR-IOV" && svm.CanCreate, "SR-IOV name input updates create availability");

                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                    await Task.Delay(60);
                    foreach (var window in windows)
                    {
                        foreach (var scale in new[] { 1d, 1.5d, 2d }) Render(window, "default", scale);
                        SetBusy(window, true);
                        Require(window.GetVisualDescendants().OfType<Button>().Where(b => b.Content?.ToString() is "Cancel" or "Apply" or "Create network")
                            .All(b => !b.IsEffectivelyEnabled), window.GetType().Name + " disables Save and Cancel while saving");
                        window.Close();
                        Require(window.IsVisible, window.GetType().Name + " rejects window close while saving");
                        SetBusy(window, false);
                        window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                        var cancel = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Cancel");
                        var position = cancel.TranslatePoint(new Point(0, 0), window)!.Value;
                        Require(position.Y >= 0 && position.Y + cancel.Bounds.Height <= window.ClientSize.Height + 1,
                            window.GetType().Name + " footer stays visible at minimum window size");
                        Render(window, "minimum", 1);
                    }
                    await bvm.SaveCommand.ExecuteAsync(null);
                    await ivm.SaveCommand.ExecuteAsync(null);
                    await svm.CreateCommand.ExecuteAsync(null);
                    Require(new[] { bvm.StatusMessage, ivm.StatusMessage, svm.StatusMessage }.All(s => s.Contains("disconnected", StringComparison.OrdinalIgnoreCase)),
                        "All three actual save commands reject disconnected synthetic connections before any network action");
                    Require(bvm.NameLabel == "Reviewed synthetic bond" && ivm.Address == "2001:db8::10/64" && svm.NameLabel == "Reviewed synthetic SR-IOV",
                        "Rejected saves preserve every editor draft");
                    foreach (var window in windows)
                    {
                        Require(window.IsVisible, window.GetType().Name + " remains open after rejected save");
                        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Cancel")
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(!window.IsVisible, window.GetType().Name + " Cancel button closes idle window");
                    }
                    File.WriteAllLines(Path.Combine(Evidence, "results.log"), checks.Prepend("PASS: .NET 10 production editor windows on Windows; synthetic disconnected pool; no profile/bootstrap initialization or API calls."));
                    Console.WriteLine($"Passed {checks.Count} editor checks. Evidence: {Evidence}");
                    theme?.Dispose(); settings?.Dispose(); lifetime.Shutdown(0);
                }
                catch (Exception error) { Fail(lifetime, error); }
            }, TimeSpan.FromMilliseconds(600));
        }
        catch (Exception error) { Fail(lifetime, error); }
    }
    static void SetBusy(Window window, bool value)
    {
        switch (window.DataContext)
        {
            case BondEditorViewModel vm: vm.IsSaving = value; break;
            case HostIpEditorViewModel vm: vm.IsSaving = value; break;
            case SriovNetworkViewModel vm: vm.IsSaving = value; break;
        }
    }
    void CheckConnectionSettings(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        var window = new SettingsWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-10000, -10000), ShowActivated = false, ShowInTaskbar = false
        };
        lifetime.MainWindow = window;
        window.FindControl<TabControl>("SettingsTabs")!.SelectedItem = window.FindControl<TabControl>("SettingsTabs")!
            .Items.OfType<TabItem>().Single(tab => tab.Header?.ToString() == "Connection");
        window.Show();
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                foreach (var (width, height) in new[] { (760, 650), (440, 400) })
                {
                    window.Width = width; window.Height = height; window.UpdateLayout();
                    var explanation = window.GetVisualDescendants().OfType<TextBlock>()
                        .Single(text => text.Text?.StartsWith("Basic/Digest applies") == true);
                    explanation.BringIntoView(); window.UpdateLayout();
                    Console.WriteLine($"Settings {width}x{height}: explanation={explanation.Bounds}, text={explanation.TextLayout.Width}x{explanation.TextLayout.Height}");
                    Render(window, $"connection-{width}", 1);
                    Require(explanation.IsEffectivelyVisible && explanation.Bounds.Width > 0,
                        $"Proxy scope explanation is visible at {width}x{height}");
                    Require(explanation.TextLayout.Height <= explanation.Bounds.Height + 1
                        && explanation.TextLayout.Width <= explanation.Bounds.Width + 1,
                        $"Proxy scope explanation wraps without clipping at {width}x{height}");
                }
                Console.WriteLine($"Passed {checks.Count} connection settings layout checks. Evidence: {Evidence}");
                window.Close(); theme?.Dispose(); settings?.Dispose(); lifetime.Shutdown(0);
            }
            catch (Exception error) { Fail(lifetime, error); }
        }, TimeSpan.FromMilliseconds(600));
    }
    void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
    static void Render(Window window, string size, double scale)
    {
        var content = window;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)(content.Bounds.Width * scale), (int)(content.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(content);
        bitmap.Save(Path.Combine(Evidence, $"{window.GetType().Name}-{size}-{scale}.png"));
    }
    void Fail(IClassicDesktopStyleApplicationLifetime lifetime, Exception error)
    { File.WriteAllText(Path.Combine(Evidence, "results.log"), string.Join("\n", checks) + "\nFAIL: " + error); Console.Error.WriteLine(error); theme?.Dispose(); settings?.Dispose(); lifetime.Shutdown(1); }
}
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This offscreen UI probe requires a Windows desktop session. Run the cross-platform tests separately.");
            return 2;
        }
        ProbeApp.ConnectionSettingsOnly = args.Contains("--connection-settings", StringComparer.Ordinal);
        try { return AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args); }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "results.log"), "FAIL: " + exception);
            return 1;
        }
    }
}
