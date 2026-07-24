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
        if (node?.Server?.Connection is not { IsConnected: true } conn)
            return Array.Empty<GeneralPropertyRow>();

        return node.Kind switch
        {
            InfraNodeKind.Pool => BuildPool(conn, node),
            InfraNodeKind.Host => BuildHost(conn, node),
            InfraNodeKind.Vm => BuildVm(conn, node),
            InfraNodeKind.Storage => BuildSr(conn, node),
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
            new("Name", pool != null ? Helpers.GetName(pool) : node.Title),
            new("Address", conn.HostnameWithPort),
            new("Coordinator", coordinator != null ? Helpers.GetName(coordinator) : "—"),
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

            rows.Add(new("UUID", pool.uuid));
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
            return new[] { new GeneralPropertyRow("Name", node.Title) };

        var rows = new List<GeneralPropertyRow>
        {
            new("Name", Helpers.GetName(host)),
            new("Address", string.IsNullOrWhiteSpace(host.address) ? host.hostname : host.address),
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

        rows.Add(new("UUID", host.uuid));
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
            return new[] { new GeneralPropertyRow("Name", node.Title) };

        var home = vm.Home();
        var rows = new List<GeneralPropertyRow>
        {
            new("Name", Helpers.GetName(vm)),
            new("Power state", FormatPowerState(vm.power_state)),
            new("Home server", home != null ? Helpers.GetName(home) : "—"),
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
            rows.Add(new("IP addresses", string.Join(", ", ips)));

        var runningTime = vm.RunningTime();
        if (runningTime != null)
            rows.Add(new("Uptime", runningTime.ToString()));

        AddVirtualisationStatus(rows, vm);

        if (!string.IsNullOrWhiteSpace(vm.AffinityServerString()) &&
            !string.Equals(vm.AffinityServerString(), home != null ? Helpers.GetName(home) : null, StringComparison.Ordinal))
        {
            rows.Add(new("Affinity", vm.AffinityServerString()));
        }

        rows.Add(new("UUID", vm.uuid));
        AddTags(rows, vm);

        var description = vm.Description();
        if (!string.IsNullOrWhiteSpace(description))
            rows.Add(new("Description", description));

        return rows;
    }

    private static IReadOnlyList<GeneralPropertyRow> BuildSr(IXenConnection conn, InfraTreeNode node)
    {
        var sr = FindSr(conn, node.OpaqueRef);
        if (sr == null)
            return new[] { new GeneralPropertyRow("Name", node.Title) };

        var rows = new List<GeneralPropertyRow>
        {
            new("Name", Helpers.GetName(sr)),
            new("Type", sr.FriendlyTypeName()),
            new("Shared", sr.shared ? "Yes" : "No")
        };

        if (sr.content_type != SR.Content_Type_ISO && sr.GetSRType(false) != SR.SRTypes.udev)
            rows.Add(new("Size", sr.SizeString()));

        var used = Util.DiskSizeString(sr.physical_utilisation);
        var free = Util.DiskSizeString(sr.FreeSpace());
        rows.Add(new("Used", used));
        rows.Add(new("Free", free));

        var scsiId = sr.GetScsiID();
        if (!string.IsNullOrWhiteSpace(scsiId))
            rows.Add(new("SCSI ID", scsiId));

        var pool = Helpers.GetPool(conn);
        if (pool != null)
            rows.Add(new("Pool", Helpers.GetName(pool)));
        else
        {
            var coordinator = Helpers.GetCoordinator(conn);
            if (coordinator != null)
                rows.Add(new("Server", Helpers.GetName(coordinator)));
        }

        rows.Add(new("UUID", sr.uuid));
        AddTags(rows, sr);

        if (!string.IsNullOrWhiteSpace(sr.name_description))
            rows.Add(new("Description", sr.name_description));

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

    private static SR? FindSr(IXenConnection conn, string? opaqueRef)
        => string.IsNullOrEmpty(opaqueRef)
            ? null
            : conn.Cache.SRs?.FirstOrDefault(sr => sr.opaque_ref == opaqueRef);
}
