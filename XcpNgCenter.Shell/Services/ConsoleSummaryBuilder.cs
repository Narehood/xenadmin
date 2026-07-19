using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Read-only console summary for the Avalonia shell.
/// Interactive RFB/VNC embedding is a follow-up (WinForms VNC stack is GDI-tied).
/// </summary>
public static class ConsoleSummaryBuilder
{
    public readonly record struct ConsoleSummary(
        IReadOnlyList<GeneralPropertyRow> Totals,
        IReadOnlyList<ConsoleItemRow> Items,
        string StatusMessage,
        string PlaceholderMessage);

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
        "Interactive VNC is not in the Avalonia preview yet. Use WinForms XCP-ng Center for live console access.");

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
            "Select a host or VM to inspect console endpoints.",
            "Interactive VNC is not in the Avalonia preview yet. Select a running VM for RFB location details, or use WinForms XCP-ng Center for live console access.");
    }

    private static ConsoleSummary BuildHost(IXenConnection conn, Host? host)
    {
        if (host == null)
            return Empty("Host not found in cache.");

        var dom0 = host.ControlDomainZero();
        if (dom0 == null)
            return Empty("Control domain not available for this host.");

        return BuildForVm(conn, dom0, objectLabel: $"Control domain ({Helpers.GetName(host)})");
    }

    private static ConsoleSummary BuildVm(IXenConnection conn, VM? vm)
    {
        if (vm == null)
            return Empty("VM not found in cache.");

        return BuildForVm(conn, vm, objectLabel: Helpers.GetName(vm));
    }

    private static ConsoleSummary BuildForVm(IXenConnection conn, VM vm, string objectLabel)
    {
        var consoles = ResolveConsoles(conn, vm);
        var items = consoles
            .OrderBy(c => ProtocolRank(c.protocol))
            .ThenBy(c => c.uuid, StringComparer.OrdinalIgnoreCase)
            .Select(c =>
            {
                var location = c.location ?? string.Empty;
                return new ConsoleItemRow(
                    ProtocolLabel(c.protocol),
                    string.IsNullOrWhiteSpace(c.uuid) ? "—" : c.uuid,
                    StatusForConsole(vm, c),
                    location,
                    canCopyLocation: !string.IsNullOrWhiteSpace(location));
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

        var status = BuildStatus(vm, rfb);
        var placeholder = vm.power_state == vm_power_state.Running && rfb != null
            ? "RFB endpoint is available. Interactive VNC rendering lands in a later Avalonia slice — use WinForms XCP-ng Center for live console today."
            : "Interactive VNC is not in the Avalonia preview yet. Use WinForms XCP-ng Center for live console access.";

        return new ConsoleSummary(totals, items, status, placeholder);
    }

    private static List<XenAPI.Console> ResolveConsoles(IXenConnection conn, VM vm)
        => conn.ResolveAll(vm.consoles).Where(c => c != null).Cast<XenAPI.Console>().ToList();

    private static string BuildStatus(VM vm, XenAPI.Console? rfb)
    {
        if (vm.power_state != vm_power_state.Running)
            return $"VM is {FormatPowerState(vm.power_state).ToLowerInvariant()} — start it to use the console.";
        if (rfb == null)
            return "No RFB (VNC) console is registered for this VM.";
        return "RFB console location ready (copy below). Live viewer coming next.";
    }

    private static string StatusForConsole(VM vm, XenAPI.Console console)
    {
        if (console.protocol == console_protocol.rfb && vm.power_state == vm_power_state.Running)
            return "Ready when interactive VNC lands";
        if (console.protocol == console_protocol.rfb)
            return "RFB present — VM not running";
        if (console.protocol == console_protocol.vt100)
            return "Serial / VT100";
        if (console.protocol == console_protocol.rdp)
            return "RDP (WinForms path)";
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
