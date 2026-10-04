using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using XcpNgCenter.Shell.Views;
using Console = System.Console;
using XenAdmin.Core;
using Task = System.Threading.Tasks.Task;

sealed partial class ProbeApp
{
    void CheckModernization(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        DispatcherTimer.RunOnce(async () =>
        {
            try
            {
                var measurements = new List<object>();
                foreach (var size in new[] { (4, 100), (16, 1000), (64, 5000) })
                    foreach (var reuse in new[] { false, true })
                        foreach (var expanded in new[] { false, true })
                            measurements.Add(await MeasureTreeRefresh(lifetime, size.Item1, size.Item2, reuse, expanded));
                var shell = typeof(MainWindow).Assembly;
                File.WriteAllText(Path.Combine(Evidence, "inventory-layout.json"), JsonSerializer.Serialize(new
                {
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    ShellSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(shell.Location))),
                    Limitations = "Synthetic cache, production TreeView/template/styles and expansion helpers; no MainViewModel, detail refresh, network, saved profile or live session.",
                    Measurements = measurements
                }, new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllLines(Path.Combine(Evidence, "results.log"), checks.Prepend(
                    "PASS: production tree layout; no profile, credentials or live pool used."));
                Console.WriteLine($"Passed {checks.Count} modernization UI checks. Evidence: {Evidence}");
                theme?.Dispose(); settings?.Dispose(); lifetime.Shutdown(0);
            }
            catch (Exception error) { Fail(lifetime, error); }
        }, TimeSpan.FromMilliseconds(100));
    }

    async Task<object> MeasureTreeRefresh(IClassicDesktopStyleApplicationLifetime lifetime, int hosts, int vms, bool reuse, bool fullyExpanded)
    {
        var connection = new XenConnection { Hostname = "192.0.2.1" };
        var changes = new List<ObjectChange>();
        changes.Add(new(typeof(Pool), "pool", new Pool { name_label = "Synthetic pool", master = new("host-0") }));
        for (var i = 0; i < hosts; i++)
            changes.Add(new(typeof(Host), $"host-{i}", new Host { uuid = $"host-uuid-{i}", name_label = $"Host {i:D3}" }));
        for (var i = 0; i < vms; i++)
            changes.Add(new(typeof(VM), $"vm-{i}", new VM { uuid = $"vm-uuid-{i}", name_label = $"Guest {i:D5}",
                power_state = vm_power_state.Running, resident_on = new($"host-{i % hosts}") }));
        connection.Cache.UpdateFrom(connection, changes);
        var server = new ServerNode { Connection = connection, IsConnected = true };
        var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-10000, -10000), ShowActivated = false, ShowInTaskbar = false };
        lifetime.MainWindow = window;
        var tree = window.FindControl<TreeView>("InfraTree")!;
        tree.IsVisible = true;
        var root = InfrastructureTreeBuilder.Build(server, connection);
        // Exercise both expanded and collapsed containers, retaining the selected guest.
        foreach (var host in root.Children.Where(n => n.Kind == InfraNodeKind.Host).Skip(1)) host.IsExpanded = fullyExpanded;
        var roots = new ObservableCollection<InfraTreeNode> { root };
        tree.ItemsSource = roots;
        tree.SelectedItem = FlattenTree(root).First(n => n.Kind == InfraNodeKind.Vm);
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        window.UpdateLayout();
        Require(tree.IsEffectivelyVisible && tree.Bounds.Height > 0, $"{hosts}/{vms}: production tree is laid out");
        var capture = typeof(MainViewModel).GetMethod("CaptureExpandState", BindingFlags.NonPublic | BindingFlags.Static)!;
        var apply = typeof(MainViewModel).GetMethod("ApplyExpandState", BindingFlags.NonPublic | BindingFlags.Static)!;
        var expandAncestors = typeof(MainViewModel).GetMethod("ExpandAncestors", BindingFlags.NonPublic | BindingFlags.Static)!;
        var queued = new Queue<Action>();
        var builds = 0;
        using var scheduler = new InventoryRefreshScheduler(queued.Enqueue, (_, _) =>
        {
            var selected = ((InfraTreeNode)tree.SelectedItem!).OpaqueRef;
            var expanded = capture.Invoke(null, [root]);
            var incoming = InfrastructureTreeBuilder.Build(server, connection);
            apply.Invoke(null, [incoming, expanded]);
            root = reuse ? InfrastructureTreeUpdater.Apply(root, incoming) : incoming;
            if (!ReferenceEquals(roots[0], root)) roots[0] = root;
            var next = FlattenTree(root).Single(n => n.OpaqueRef == selected);
            expandAncestors.Invoke(null, [root, next]);
            tree.SelectedItem = next;
            window.UpdateLayout();
            builds++;
        });
        var times = new List<double>();
        var allocations = new List<long>();
        for (var sample = 0; sample < 7; sample++)
        {
            connection.Resolve(new XenRef<VM>("vm-0")).name_label = $"Changed guest {sample}";
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var update = 0; update < 200; update++) scheduler.Request(server, connection);
            Require(queued.Count == 1, $"{hosts}/{vms} sample {sample}: burst queues one UI refresh");
            queued.Dequeue()();
            watch.Stop();
            if (sample >= 2) { times.Add(watch.Elapsed.TotalMilliseconds); allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before); }
            Require(((InfraTreeNode)tree.SelectedItem!).OpaqueRef == "vm-0", $"{hosts}/{vms} sample {sample}: selected guest survives replacement");
            Require(((InfraTreeNode)tree.SelectedItem!).Title == $"Changed guest {sample}", $"{hosts}/{vms} sample {sample}: fresh guest metadata reaches the selected node");
            Require(root.Children.Where(n => n.Kind == InfraNodeKind.Host).Skip(1).All(n => n.IsExpanded == fullyExpanded),
                $"{hosts}/{vms} sample {sample}: unrelated host expansion is preserved ({fullyExpanded})");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        Require(builds == 7, $"{hosts}/{vms}: seven bursts produce seven builds");
        var selectedBeforeMove = tree.SelectedItem;
        connection.Resolve(new XenRef<VM>("vm-0")).resident_on = new("host-1");
        scheduler.Request(server, connection);
        queued.Dequeue()();
        var destination = FlattenTree(root).Single(n => n.OpaqueRef == "host-1");
        Require(destination.IsExpanded && destination.Children.Contains((InfraTreeNode)tree.SelectedItem!),
            $"{hosts}/{vms}: migration expands the selected guest's destination path");
        if (reuse) Require(ReferenceEquals(selectedBeforeMove, tree.SelectedItem),
            $"{hosts}/{vms}: migration retains the selected guest instance");
        Render(window, $"inventory-{hosts}-{vms}-{(reuse ? "reuse" : "replace")}-{(fullyExpanded ? "expanded" : "collapsed")}", 1);
        window.Close();
        times.Sort();
        Console.WriteLine($"Tree layout {hosts} hosts/{vms} VMs, reuse={reuse}: median {times[times.Count / 2]:F3} ms");
        return new { Hosts = hosts, Vms = vms, ReusesNodes = reuse, FullyExpanded = fullyExpanded, RenderScaling = window.RenderScaling, EventsPerBurst = 200, Warmups = 2, Samples = times,
            MedianMilliseconds = times[times.Count / 2], AllocatedBytes = allocations };
    }

    static IEnumerable<InfraTreeNode> FlattenTree(InfraTreeNode root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var node in FlattenTree(child)) yield return node;
    }
}
