using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Model;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

public static class GeneralSummaryBuilder
{
    public static IReadOnlyList<GeneralPropertyRow> Build(InfraTreeNode? node)
    {
        if (node?.Server is null)
            return Array.Empty<GeneralPropertyRow>();

        if (node.Server.Connection is not { IsConnected: true } conn)
        {
            var server = node.Server;
            return new[]
            {
                new GeneralPropertyRow("Name", string.IsNullOrWhiteSpace(server.Name) ? server.Address : server.Name),
                new GeneralPropertyRow("Address", server.Address),
                new GeneralPropertyRow("Status", server.Status),
                new GeneralPropertyRow("Detail", string.IsNullOrWhiteSpace(server.Summary) ? "—" : server.Summary),
                new GeneralPropertyRow("Username", server.Username)
            };
        }

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
        var srs = conn.Cache.SRs?.Count(sr => sr != null && !sr.IsToolsSR() && sr.Show(false)) ?? 0;
        var networks = conn.Cache.Networks?.Count(n => n != null && n.Show(false)) ?? 0;

        var rows = new List<GeneralPropertyRow>
        {
            new("Name", IdentifierPrivacy.ClusterName(pool != null ? Helpers.GetName(pool) : node.Title)),
            new("Address", IdentifierPrivacy.Address(conn.HostnameWithPort)),
            new("Coordinator", coordinator != null ? IdentifierPrivacy.ServerName(Helpers.GetName(coordinator)) : "—"),
            new("Hosts", hosts.ToString()),
            new("VMs", vms.ToString()),
            new("Storage repositories", srs.ToString()),
            new("Networks", networks.ToString())
        };

        if (pool != null)
        {
            rows.Add(new("HA", pool.ha_enabled ? "Enabled" : "Disabled"));
            if (pool.ha_enabled && pool.ha_host_failures_to_tolerate > 0)
                rows.Add(new("HA host failures to tolerate", pool.ha_host_failures_to_tolerate.ToString()));

            rows.Add(new("UUID", IdentifierPrivacy.Uuid(pool.uuid)));
            AddTags(rows, pool);

            if (!string.IsNullOrWhiteSpace(pool.name_description))
                rows.Add(new("Description", pool.name_description));
        }

        return rows;
    }

    private static IReadOnlyList<GeneralPropertyRow> BuildHost(IXenConnection conn, InfraTreeNode node)
    {
        var host = FindHost(conn, node.OpaqueRef);
        if (host == null)
            return new[] { new GeneralPropertyRow("Name", IdentifierPrivacy.ServerName(node.Title)) };

        var rows = new List<GeneralPropertyRow>
        {
            new("Name", IdentifierPrivacy.ServerName(Helpers.GetName(host))),
            new("Address", IdentifierPrivacy.Address(string.IsNullOrWhiteSpace(host.address) ? host.hostname : host.address)),
            new("Role", Helpers.HostIsCoordinator(host) ? "Coordinator" : "Member"),
            new("Product", FormatHostProduct(host)),
            new("Enabled", FormatHostEnabled(host)),
            new("CPUs", host.CpuCount().ToString())
        };

        var metrics = conn.Resolve(host.metrics);
        if (metrics != null)
        {
            rows.Add(new("Memory total", Util.MemorySizeStringSuitableUnits(metrics.memory_total, true)));
            rows.Add(new("Memory free", Util.MemorySizeStringSuitableUnits(host.memory_available_calc(), true)));
        }

        var uptime = host.Uptime();
        if (uptime != null)
            rows.Add(new("Server uptime", uptime.ToString()));

        var agentUptime = host.AgentUptime();
        if (agentUptime != null)
            rows.Add(new("Toolstack uptime", agentUptime.ToString()));

        var iqn = host.GetIscsiIqn();
        if (!string.IsNullOrWhiteSpace(iqn))
            rows.Add(new("iSCSI IQN", iqn));

        rows.Add(new("Autoboot VMs", host.GetVmAutostartEnabled() ? "Enabled" : "Disabled"));

        if (host.external_auth_type == Auth.AUTH_TYPE_AD &&
            !string.IsNullOrWhiteSpace(host.external_auth_service_name))
        {
            rows.Add(new("AD domain", host.external_auth_service_name));
        }

        rows.Add(new("UUID", IdentifierPrivacy.Uuid(host.uuid)));
        AddTags(rows, host);

        var description = host.Description();
        if (!string.IsNullOrWhiteSpace(description))
            rows.Add(new("Description", description));

        return rows;
    }

    private static IReadOnlyList<GeneralPropertyRow> BuildVm(IXenConnection conn, InfraTreeNode node)
    {
        var vm = FindVm(conn, node.OpaqueRef);
        if (vm == null)
            return new[] { new GeneralPropertyRow("Name", IdentifierPrivacy.VmName(node.Title)) };

        var home = vm.Home();
        var rows = new List<GeneralPropertyRow>
        {
            new("Name", IdentifierPrivacy.VmName(Helpers.GetName(vm))),
            new("Power state", FormatPowerState(vm.power_state)),
            new("Home server", home != null ? IdentifierPrivacy.ServerName(Helpers.GetName(home)) : "—"),
            new("OS", string.IsNullOrWhiteSpace(vm.GetOSName()) ? "—" : vm.GetOSName()),
            new("Virtualization mode", vm.IsHVM() ? "HVM" : "PV"),
            new("vCPUs", FormatVcpus(vm)),
            new("Memory", Util.MemorySizeStringSuitableUnits(vm.memory_static_max, true))
        };

        if (vm.memory_dynamic_min != vm.memory_dynamic_max ||
            vm.memory_dynamic_max != vm.memory_static_max)
        {
            rows.Add(new(
                "Dynamic memory",
                $"{Util.MemorySizeStringSuitableUnits(vm.memory_dynamic_min, true)} – {Util.MemorySizeStringSuitableUnits(vm.memory_dynamic_max, true)}"));
        }

        var ips = CollectVmIps(conn, vm);
        if (ips.Count > 0)
        {
            var display = IdentifierPrivacy.HideIpAddresses
                ? IdentifierPrivacy.Placeholder
                : string.Join(", ", ips);
            rows.Add(new("IP addresses", display));
        }

        var runningTime = vm.RunningTime();
        if (runningTime != null)
            rows.Add(new("Uptime", runningTime.ToString()));

        AddVirtualisationStatus(rows, vm);

        if (!string.IsNullOrWhiteSpace(vm.AffinityServerString()) &&
            !string.Equals(vm.AffinityServerString(), home != null ? Helpers.GetName(home) : null, StringComparison.Ordinal))
        {
            rows.Add(new("Affinity", vm.AffinityServerString()));
        }

        rows.Add(new("UUID", IdentifierPrivacy.Uuid(vm.uuid)));
        AddTags(rows, vm);

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

    private static string FormatHostEnabled(Host host)
    {
        if (!host.IsLive())
            return "Not live";
        if (!host.enabled)
            return host.MaintenanceMode() ? "Maintenance mode" : "Disabled";
        return "Yes";
    }

    private static string FormatPowerState(vm_power_state state) => state switch
    {
        vm_power_state.Running => "Running",
        vm_power_state.Halted => "Halted",
        vm_power_state.Paused => "Paused",
        vm_power_state.Suspended => "Suspended",
        _ => state.ToString()
    };

    private static string FormatVcpus(VM vm)
    {
        if (vm.VCPUs_max > 0 && vm.VCPUs_max != vm.VCPUs_at_startup)
            return $"{vm.VCPUs_at_startup} (max {vm.VCPUs_max})";
        return vm.VCPUs_at_startup.ToString();
    }

    private static List<string> CollectVmIps(IXenConnection conn, VM vm)
    {
        var ips = new List<string>();
        foreach (var vif in conn.ResolveAll(vm.VIFs))
        {
            if (vif == null)
                continue;
            var network = conn.Resolve(vif.network);
            if (network != null && network.IsGuestInstallerNetwork())
                continue;

            foreach (var ip in vif.IPAddresses())
            {
                if (!string.IsNullOrWhiteSpace(ip) && !ips.Contains(ip))
                    ips.Add(ip);
            }
        }

        return ips;
    }

    private static void AddVirtualisationStatus(List<GeneralPropertyRow> rows, VM vm)
    {
        if (vm.power_state != vm_power_state.Running)
            return;

        var gm = vm.Connection?.Resolve(vm.guest_metrics);
        if (gm == null)
        {
            rows.Add(new("Guest tools", "Not reported"));
            return;
        }

        var drivers = gm.PV_drivers_installed()
            ? (gm.PV_drivers_up_to_date ? "Installed (up to date)" : "Installed (update available)")
            : "Not installed";
        rows.Add(new("Guest tools", drivers));
    }

    private static void AddTags(List<GeneralPropertyRow> rows, IXenObject xenObject)
    {
        var tags = Tags.GetTags(xenObject);
        if (tags is { Length: > 0 })
            rows.Add(new("Tags", string.Join(", ", tags.OrderBy(t => t))));
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
