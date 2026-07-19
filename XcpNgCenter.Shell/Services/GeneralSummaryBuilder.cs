using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

public static class GeneralSummaryBuilder
{
    public static IReadOnlyList<GeneralPropertyRow> Build(InfraTreeNode? node)
    {
        if (node?.Server?.Connection is not { IsConnected: true } conn)
            return Array.Empty<GeneralPropertyRow>();

        return node.Kind switch
        {
            InfraNodeKind.Pool => BuildPool(conn, node),
            InfraNodeKind.Host => BuildHost(conn, node),
            InfraNodeKind.Vm => BuildVm(conn, node),
            _ => Array.Empty<GeneralPropertyRow>()
        };
    }

    private static IReadOnlyList<GeneralPropertyRow> BuildPool(IXenConnection conn, InfraTreeNode node)
    {
        var pool = FindPool(conn, node.OpaqueRef) ?? Helpers.GetPoolOfOne(conn);
        var coordinator = Helpers.GetCoordinator(conn);
        var hosts = conn.Cache.Hosts?.Length ?? 0;
        var vms = conn.Cache.VMs?.Count(vm => vm.IsRealVm()) ?? 0;

        var rows = new List<GeneralPropertyRow>
        {
            new("Name", pool != null ? Helpers.GetName(pool) : node.Title),
            new("Address", conn.HostnameWithPort),
            new("Coordinator", coordinator != null ? Helpers.GetName(coordinator) : "—"),
            new("Hosts", hosts.ToString()),
            new("VMs", vms.ToString())
        };

        if (pool != null)
        {
            rows.Add(new("HA", pool.ha_enabled ? "Enabled" : "Disabled"));
            if (!string.IsNullOrWhiteSpace(pool.name_description))
                rows.Add(new("Description", pool.name_description));
        }

        return rows;
    }

    private static IReadOnlyList<GeneralPropertyRow> BuildHost(IXenConnection conn, InfraTreeNode node)
    {
        var host = FindHost(conn, node.OpaqueRef);
        if (host == null)
            return new[] { new GeneralPropertyRow("Name", node.Title) };

        var rows = new List<GeneralPropertyRow>
        {
            new("Name", Helpers.GetName(host)),
            new("Address", string.IsNullOrWhiteSpace(host.address) ? host.hostname : host.address),
            new("Role", Helpers.HostIsCoordinator(host) ? "Coordinator" : "Member"),
            new("Product", FormatHostProduct(host)),
            new("CPUs", host.CpuCount().ToString())
        };

        var metrics = conn.Resolve(host.metrics);
        if (metrics != null)
        {
            rows.Add(new("Memory total", Util.MemorySizeStringSuitableUnits(metrics.memory_total, true)));
            rows.Add(new("Memory free", Util.MemorySizeStringSuitableUnits(host.memory_available_calc(), true)));
        }

        var description = host.Description();
        if (!string.IsNullOrWhiteSpace(description))
            rows.Add(new("Description", description));

        return rows;
    }

    private static IReadOnlyList<GeneralPropertyRow> BuildVm(IXenConnection conn, InfraTreeNode node)
    {
        var vm = FindVm(conn, node.OpaqueRef);
        if (vm == null)
            return new[] { new GeneralPropertyRow("Name", node.Title) };

        var home = vm.Home();
        var rows = new List<GeneralPropertyRow>
        {
            new("Name", Helpers.GetName(vm)),
            new("Power state", vm.power_state.ToString()),
            new("Home server", home != null ? Helpers.GetName(home) : "—"),
            new("vCPUs", vm.VCPUs_at_startup.ToString()),
            new("Memory", Util.MemorySizeStringSuitableUnits(vm.memory_static_max, true))
        };

        var description = vm.Description();
        if (!string.IsNullOrWhiteSpace(description))
            rows.Add(new("Description", description));

        return rows;
    }

    private static string FormatHostProduct(Host host)
    {
        var brand = host.ProductBrand() ?? "XCP-ng";
        var version = host.ProductVersionText() ?? host.ProductVersion() ?? string.Empty;
        return string.IsNullOrWhiteSpace(version) ? brand : $"{brand} {version}";
    }

    private static Pool? FindPool(IXenConnection conn, string? opaqueRef)
        => string.IsNullOrEmpty(opaqueRef)
            ? null
            : conn.Cache.Pools?.FirstOrDefault(p => p.opaque_ref == opaqueRef);

    private static Host? FindHost(IXenConnection conn, string? opaqueRef)
        => string.IsNullOrEmpty(opaqueRef)
            ? null
            : conn.Cache.Hosts?.FirstOrDefault(h => h.opaque_ref == opaqueRef);

    private static VM? FindVm(IXenConnection conn, string? opaqueRef)
        => string.IsNullOrEmpty(opaqueRef)
            ? null
            : conn.Cache.VMs?.FirstOrDefault(v => v.opaque_ref == opaqueRef);
}
