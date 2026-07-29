using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Console summary + live RFB target resolution for the Avalonia shell.
/// </summary>
public static class ConsoleSummaryBuilder
{
    public readonly record struct ConsoleSummary(
        IReadOnlyList<GeneralPropertyRow> Totals,
        IReadOnlyList<ConsoleItemRow> Items,
        string StatusMessage,
        string PlaceholderMessage,
        LiveRfbTarget? LiveTarget);

    public static ConsoleSummary Build(InfraTreeNode? node)
    {
        if (node?.Server?.Connection is not { IsConnected: true } conn)
            return Empty("Connect to a server to inspect consoles.");

        return node.Kind switch
        {
            InfraNodeKind.Pool => BuildPool(conn),
            InfraNodeKind.Host => BuildHost(conn, FindHost(conn, node.OpaqueRef)),
            InfraNodeKind.Vm => BuildVm(conn, FindVm(conn, node.OpaqueRef)),
            _ => Empty("Select a pool, host, or VM.")
        };
    }

    private static ConsoleSummary Empty(string status) => new(
        Array.Empty<GeneralPropertyRow>(),
        Array.Empty<ConsoleItemRow>(),
        status,
        "Select a running VM or host to open the RFB console preview.",
        null);

    private static ConsoleSummary BuildPool(IXenConnection conn)
    {
        var running = (conn.Cache.VMs ?? Array.Empty<VM>())
            .Where(vm => vm.IsRealVm() && vm.power_state == vm_power_state.Running)
            .ToList();

        var withRfb = 0;
        foreach (var vm in running)
        {
            if (ResolveConsoles(conn, vm).Any(c => c.protocol == console_protocol.rfb))
                withRfb++;
        }

        var totals = new List<GeneralPropertyRow>
        {
            new("Running VMs", running.Count.ToString()),
            new("With RFB console", withRfb.ToString())
        };

        return new ConsoleSummary(
            totals,
            Array.Empty<ConsoleItemRow>(),
            "Select a host or VM to open a console preview.",
            "Select a running VM (or host control domain) to start the RFB viewer.",
            null);
    }

    private static ConsoleSummary BuildHost(IXenConnection conn, Host? host)
    {
        if (host == null)
            return Empty("Host not found in cache.");

        var dom0 = host.ControlDomainZero();
        if (dom0 == null)
            return Empty("Control domain not available for this host.");

        // Never keep a live RFB CONNECT open to a host that is power-cycling.
        // An open dom0 console (auto-started on host select) can interfere with
        // orderly reboot/shutdown — XO and SSH do not hold this path open.
        if (ShellStatusIcons.TryGetHostPowerProgress(host, out var powerTip))
        {
            var busy = BuildForVm(conn, dom0, objectLabel: $"Control domain ({IdentifierPrivacy.ServerName(Helpers.GetName(host))})", allowLive: false);
            return busy with
            {
                StatusMessage = $"{powerTip} Console disconnected so the host can finish rebooting.",
                PlaceholderMessage = "Host console is paused during reboot/shutdown."
            };
        }

        var metrics = conn.Resolve(host.metrics);
        if (metrics is { live: false })
        {
            var offline = BuildForVm(conn, dom0, objectLabel: $"Control domain ({IdentifierPrivacy.ServerName(Helpers.GetName(host))})", allowLive: false);
            return offline with
            {
                StatusMessage = "Host is offline — console unavailable.",
                PlaceholderMessage = "Host console will be available when the server is back online."
            };
        }

        return BuildForVm(conn, dom0, objectLabel: $"Control domain ({IdentifierPrivacy.ServerName(Helpers.GetName(host))})");
    }

    private static ConsoleSummary BuildVm(IXenConnection conn, VM? vm)
    {
        if (vm == null)
            return Empty("VM not found in cache.");

        return BuildForVm(conn, vm, objectLabel: IdentifierPrivacy.VmName(Helpers.GetName(vm)));
    }

    private static ConsoleSummary BuildForVm(IXenConnection conn, VM vm, string objectLabel, bool allowLive = true)
    {
        var consoles = ResolveConsoles(conn, vm);
        var items = consoles
            .OrderBy(c => ProtocolRank(c.protocol))
            .ThenBy(c => c.uuid, StringComparer.OrdinalIgnoreCase)
            .Select(c =>
            {
                var location = c.location ?? string.Empty;
                var displayLocation = IdentifierPrivacy.HideIpAddresses
                    ? IdentifierPrivacy.Placeholder
                    : location;
                return new ConsoleItemRow(
                    ProtocolLabel(c.protocol),
                    string.IsNullOrWhiteSpace(c.uuid) ? "—" : IdentifierPrivacy.Uuid(c.uuid),
                    StatusForConsole(vm, c),
                    displayLocation,
                    canCopyLocation: !string.IsNullOrWhiteSpace(location)
                                     && !IdentifierPrivacy.HideIpAddresses);
            })
            .ToList();

        var rfb = consoles.FirstOrDefault(c => c.protocol == console_protocol.rfb);
        var totals = new List<GeneralPropertyRow>
        {
            new("Object", objectLabel),
            new("Power state", FormatPowerState(vm.power_state)),
            new("Consoles", consoles.Count.ToString()),
            new("RFB", rfb != null ? "Available" : "Not available")
        };

        LiveRfbTarget? live = null;
        if (allowLive
            && vm.power_state == vm_power_state.Running
            && rfb != null
            && !string.IsNullOrWhiteSpace(rfb.location))
        {
            live = new LiveRfbTarget(
                conn,
                rfb,
                objectLabel,
                objectLabel,
                string.IsNullOrWhiteSpace(vm.uuid) ? rfb.uuid ?? objectLabel : vm.uuid);
        }

        var status = BuildStatus(vm, rfb, live != null);
        var placeholder = live != null
            ? "Connecting RFB preview…"
            : vm.power_state == vm_power_state.Running
                ? "No RFB console is available for this object."
                : "Start the VM to open the RFB console preview.";

        return new ConsoleSummary(totals, items, status, placeholder, live);
    }

    private static List<XenAPI.Console> ResolveConsoles(IXenConnection conn, VM vm)
        => conn.ResolveAll(vm.consoles).Where(c => c != null).Cast<XenAPI.Console>().ToList();

    private static string BuildStatus(VM vm, XenAPI.Console? rfb, bool canLive)
    {
        if (vm.power_state != vm_power_state.Running)
            return $"VM is {FormatPowerState(vm.power_state).ToLowerInvariant()} — start it to use the console.";
        if (rfb == null)
            return "No RFB (VNC) console is registered for this VM.";
        if (canLive)
            return "RFB console available — live preview above (click to focus for input).";
        return "RFB console location ready (copy below).";
    }

    private static string StatusForConsole(VM vm, XenAPI.Console console)
    {
        if (console.protocol == console_protocol.rfb && vm.power_state == vm_power_state.Running)
            return "Ready for live preview";
        if (console.protocol == console_protocol.rfb)
            return "RFB present — VM not running";
        if (console.protocol == console_protocol.vt100)
            return "Serial / VT100";
        if (console.protocol == console_protocol.rdp)
            return "RDP (deferred)";
        return console.protocol.ToString();
    }

    private static int ProtocolRank(console_protocol protocol) => protocol switch
    {
        console_protocol.rfb => 0,
        console_protocol.vt100 => 1,
        console_protocol.rdp => 2,
        _ => 9
    };

    private static string ProtocolLabel(console_protocol protocol) => protocol switch
    {
        console_protocol.rfb => "RFB (VNC)",
        console_protocol.vt100 => "VT100",
        console_protocol.rdp => "RDP",
        _ => protocol.ToString().ToUpperInvariant()
    };

    private static string FormatPowerState(vm_power_state state) => state switch
    {
        vm_power_state.Running => "Running",
        vm_power_state.Halted => "Halted",
        vm_power_state.Paused => "Paused",
        vm_power_state.Suspended => "Suspended",
        _ => state.ToString()
    };

    private static Host? FindHost(IXenConnection conn, string? opaqueRef)
        => string.IsNullOrEmpty(opaqueRef)
            ? null
            : conn.Cache.Hosts?.FirstOrDefault(h => h.opaque_ref == opaqueRef);

    private static VM? FindVm(IXenConnection conn, string? opaqueRef)
        => string.IsNullOrEmpty(opaqueRef)
            ? null
            : conn.Cache.VMs?.FirstOrDefault(v => v.opaque_ref == opaqueRef);
}
