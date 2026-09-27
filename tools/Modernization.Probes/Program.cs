using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Rfb;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Console = System.Console;

// This executable has no ShellBootstrap, saved profiles, credentials, or live
// sessions. All XenAPI objects are synthetic and only enter the local cache.
try
{
    var options = Options.Parse(args);
    if (options.Help)
    {
        Console.WriteLine("Modernization.Probes [--output results.json] [--samples 30] [--warmup 5] [--label label]");
        return 0;
    }

    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    AppBuilder.Configure<Application>()
        .UseStandardRuntimePlatformSubsystem()
        .UseWindowingSubsystem(() => { }, "Offscreen")
        .UseSkia()
        .SetupWithoutStarting();

    var results = new List<Measurement>();
    foreach (var (hosts, vms) in new[] { (4, 100), (16, 1000), (64, 1000), (64, 5000) })
    {
        var (server, connection) = SyntheticInventory.Create(hosts, vms);
        InfraTreeNode? tree = null;
        results.Add(Measure($"inventory-{hosts}hosts-{vms}vms", "one complete tree build", options,
            () => tree = InfrastructureTreeBuilder.Build(server, connection)));
        var nodes = Flatten(tree!).ToArray();
        Require(nodes.Count(n => n.Kind == InfraNodeKind.Vm) == vms, "VMs were dropped or duplicated");
        Require(nodes.Count(n => n.Kind == InfraNodeKind.Host) == hosts, "Hosts were dropped or duplicated");
        Require(nodes.Count(n => n.Kind == InfraNodeKind.Storage) == hosts + 1, "SRs were dropped or duplicated");
        Require(nodes.Where(n => n.OpaqueRef != null).Select(n => n.OpaqueRef).Distinct().Count()
            == nodes.Count(n => n.OpaqueRef != null), "Object references were duplicated");
    }

    foreach (var (width, height) in new[] { (1920, 1080), (3840, 2160) })
    {
        using var framebuffer = new AvaloniaRfbFramebuffer("Synthetic console", "synthetic-console");
        var presented = 0;
        framebuffer.FramePresented += () => presented++;
        framebuffer.DesktopSize(width, height);
        Dispatcher.UIThread.RunJobs();
        var pixels = new byte[width * height * 4];
        // An opaque constant color keeps the expected image deterministic.
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x31;
            pixels[index + 1] = 0x62;
            pixels[index + 2] = 0x93;
            pixels[index + 3] = 0xff;
        }

        void Present()
        {
            framebuffer.FrameBufferUpdate();
            Dispatcher.UIThread.RunJobs();
        }

        results.Add(Measure($"console-{width}x{height}-raw", "one raw full frame and UI bitmap flush", options, () =>
        {
            framebuffer.DrawImage(pixels, 0, width * 4, 0, 0, width, height);
            Present();
        }));
        VerifyPixel(framebuffer, 0x31, 0x62, 0x93);

        results.Add(Measure($"console-{width}x{height}-small-damage", "one 64x32 update and UI bitmap flush", options, () =>
        {
            framebuffer.FillRectangle(0, 0, 64, 32, new RfbColor(0x93, 0x62, 0x31));
            Present();
        }));
        VerifyPixel(framebuffer, 0x31, 0x62, 0x93);

        var beforeBurst = presented;
        results.Add(Measure($"console-{width}x{height}-burst", "20 queued small updates and one UI drain", options, () =>
        {
            for (var update = 0; update < 20; update++)
            {
                framebuffer.FillRectangle(update * 32, 64, 32, 16, new RfbColor((byte)update, 0x62, 0x31));
                framebuffer.FrameBufferUpdate();
            }
            Dispatcher.UIThread.RunJobs();
        }));
        Require(presented - beforeBurst == options.Warmup + options.Samples,
            "Queued framebuffer updates did not coalesce into one presentation per burst");
        VerifyPixel(framebuffer, 0x31, 0x62, 19, 19 * 32, 64);

        results.Add(Measure($"console-{width}x{height}-scroll", "one overlapping full-width CopyRect and UI flush", options, () =>
        {
            framebuffer.CopyRectangle(0, 1, width, height - 1, 0, 0);
            Present();
        }));
        // Restore a known image before checking overlap semantics outside timing.
        framebuffer.DrawImage(pixels, 0, width * 4, 0, 0, width, height);
        framebuffer.FillRectangle(0, 1, width, 1, new RfbColor(0xa1, 0xb2, 0xc3));
        framebuffer.CopyRectangle(0, 1, width, height - 1, 0, 0);
        Present();
        VerifyPixel(framebuffer, 0xc3, 0xb2, 0xa1);
        VerifyPixel(framebuffer, 0x31, 0x62, 0x93, 0, 1);
    }

    using (var framebuffer = new AvaloniaRfbFramebuffer("Synthetic resize", "synthetic-resize"))
    {
        var resized = 0;
        framebuffer.DesktopResized += (_, _) => resized++;
        results.Add(Measure("console-resize", "1080p to 1440p resize cycle with UI flushes", options, () =>
        {
            framebuffer.DesktopSize(1920, 1080);
            Dispatcher.UIThread.RunJobs();
            framebuffer.DesktopSize(2560, 1440);
            Dispatcher.UIThread.RunJobs();
        }));
        Require(resized == 2 * (options.Warmup + options.Samples), "Desktop resize notification missing");
        Require(framebuffer.Bitmap?.PixelSize == new PixelSize(2560, 1440), "Final bitmap size is incorrect");
    }

    var shell = typeof(InfrastructureTreeBuilder).Assembly;
    var report = new
    {
        SchemaVersion = 1,
        CapturedAtUtc = DateTimeOffset.UtcNow,
        options.Label,
        Runtime = RuntimeInformation.FrameworkDescription,
        OS = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        LogicalProcessors = Environment.ProcessorCount,
        ServerGC = System.Runtime.GCSettings.IsServerGC,
        TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default",
        ShellVersion = shell.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        ShellSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(shell.Location))),
        options.Warmup,
        options.Samples,
        Scope = "Synthetic in-memory XenAPI inventory; offscreen Skia CPU framebuffer, no network, UI layout, GPU, or live pool.",
        AllocationScope = "Managed bytes allocated on the measuring thread; excludes native bitmap buffers and other threads.",
        Measurements = results
    };
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    if (options.Output != null)
    {
        var path = Path.GetFullPath(options.Output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json + Environment.NewLine);
        Console.WriteLine($"Report: {path}");
    }
    else
    {
        Console.WriteLine(json);
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static Measurement Measure(string name, string operation, Options options, Action action)
{
    for (var index = 0; index < options.Warmup; index++) action();
    // Normalize only between cases. Collections during sampling remain part of
    // the workload; collecting between every sample would hide allocation cost.
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var elapsed = new double[options.Samples];
    var allocations = new long[options.Samples];
    var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
    for (var index = 0; index < options.Samples; index++)
    {
        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        action();
        elapsed[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocations[index] = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
    }
    var ordered = elapsed.Order().ToArray();
    var result = new Measurement(name, operation, ordered[0],
        ordered.Length % 2 == 0 ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2 : ordered[ordered.Length / 2],
        ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1], ordered[^1], allocations.Average(),
        Enumerable.Range(0, 3).Select(generation => GC.CollectionCount(generation) - collections[generation]).ToArray(),
        elapsed, allocations);
    Console.Error.WriteLine($"{name}: median {result.MedianMs:F3} ms; p95 {result.P95Ms:F3} ms; {result.MeanAllocatedBytes:F0} B/op");
    return result;
}

static IEnumerable<InfraTreeNode> Flatten(InfraTreeNode node)
{
    yield return node;
    foreach (var child in node.Children)
        foreach (var descendant in Flatten(child))
            yield return descendant;
}

static void VerifyPixel(AvaloniaRfbFramebuffer framebuffer, byte blue, byte green, byte red, int x = 0, int y = 0)
{
    using var bitmap = framebuffer.Bitmap!.Lock();
    var pixel = new byte[4];
    Marshal.Copy(IntPtr.Add(bitmap.Address, y * bitmap.RowBytes + x * 4), pixel, 0, 4);
    Require(pixel[0] == blue && pixel[1] == green && pixel[2] == red, "Presented pixel differs from expected output");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed record Measurement(string Name, string Operation, double MinMs, double MedianMs, double P95Ms,
    double MaxMs, double MeanAllocatedBytes, int[] GcCollections, double[] SamplesMs, long[] AllocatedBytes);

internal sealed record Options(string? Output, int Samples, int Warmup, string? Label, bool Help)
{
    public static Options Parse(string[] args)
    {
        string? output = null, label = null;
        var samples = 30;
        var warmup = 5;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "--help" or "-h") return new(null, samples, warmup, null, true);
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}");
            var argument = args[index];
            var value = args[++index];
            switch (argument)
            {
                case "--output": output = value; break;
                case "--label": label = value; break;
                case "--samples" when int.TryParse(value, out var count) && count is >= 5 and <= 10000: samples = count; break;
                case "--warmup" when int.TryParse(value, out var count) && count is >= 1 and <= 1000: warmup = count; break;
                default: throw new ArgumentException($"Invalid option: {argument} {value}");
            }
        }
        return new(output, samples, warmup, label, false);
    }
}

internal static class SyntheticInventory
{
    public static (ServerNode Server, XenConnection Connection) Create(int hostCount, int vmCount)
    {
        var connection = new XenConnection { Hostname = "192.0.2.1" };
        var changes = new List<ObjectChange>();
        void Add<T>(string reference, T value) where T : XenObject<T> => changes.Add(new(typeof(T), reference, value));
        Add("pool", new Pool { name_label = "Synthetic pool", master = new XenRef<Host>("host-0") });
        for (var index = 0; index < hostCount; index++)
        {
            Add($"host-{index}", new Host { name_label = $"Host {index:D3}", address = $"192.0.2.{index + 1}" });
            Add($"local-{index}", new SR
            {
                name_label = $"Local {index:D3}", type = "lvm", physical_size = 1L << 40,
                PBDs = [new XenRef<PBD>($"pbd-{index}")]
            });
            Add($"pbd-{index}", new PBD
            {
                SR = new XenRef<SR>($"local-{index}"), host = new XenRef<Host>($"host-{index}"), currently_attached = true
            });
        }
        Add("shared", new SR
        {
            name_label = "Shared", type = "nfs", shared = true, physical_size = 8L << 40,
            PBDs = [new XenRef<PBD>("shared-pbd")]
        });
        Add("shared-pbd", new PBD { SR = new XenRef<SR>("shared"), host = new XenRef<Host>("host-0"), currently_attached = true });
        for (var index = 0; index < vmCount; index++)
        {
            var running = index % 5 != 0;
            Add($"vm-{index}", new VM
            {
                name_label = $"VM {vmCount - index:D5}", uuid = $"synthetic-vm-{index}",
                power_state = running ? vm_power_state.Running : vm_power_state.Halted,
                resident_on = running ? new XenRef<Host>($"host-{index % hostCount}") : new XenRef<Host>("OpaqueRef:NULL")
            });
        }
        Add("template", new VM { is_a_template = true });
        Add("snapshot", new VM { is_a_snapshot = true });
        Add("control-domain", new VM { is_control_domain = true });
        connection.Cache.UpdateFrom(connection, changes);
        return (new ServerNode { Connection = connection, IsConnected = true }, connection);
    }
}
