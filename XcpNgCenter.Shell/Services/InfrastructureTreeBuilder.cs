using System.Collections.ObjectModel;
using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

public static class InfrastructureTreeBuilder
{
    public static InfraTreeNode Build(ServerNode server, IXenConnection conn)
    {
        var pool = Helpers.GetPoolOfOne(conn);
        var poolName = pool != null ? Helpers.GetName(pool) : conn.Name;
        if (string.IsNullOrWhiteSpace(poolName))
            poolName = conn.Hostname;

        var hosts = (conn.Cache.Hosts ?? Array.Empty<Host>())
            .OrderBy(h => Helpers.HostIsCoordinator(h) ? 0 : 1)
            .ThenBy(h => Helpers.GetName(h), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var vms = (conn.Cache.VMs ?? Array.Empty<VM>())
            .Where(vm => vm.IsRealVm())
            .OrderBy(vm => Helpers.GetName(vm), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (poolIcon, poolTip) = ShellStatusIcons.ForPool(conn);
        var root = new InfraTreeNode
        {
            Kind = InfraNodeKind.Pool,
            Title = poolName,
            Subtitle = conn.HostnameWithPort,
            Detail = $"{hosts.Count} host(s), {vms.Count} VM(s)",
            Server = server,
            OpaqueRef = pool?.opaque_ref,
            IsExpanded = true,
            ShowStatusIcon = true,
            StatusIcon = poolIcon,
            StatusTooltip = poolTip
        };

        var placed = new HashSet<string>(StringComparer.Ordinal);
        var allSrs = VisibleSrs(conn).ToList();
        var placedSrs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var host in hosts)
        {
            var hostVms = vms
                .Where(vm => SameHost(vm.Home(), host))
                .ToList();

            foreach (var vm in hostVms)
                placed.Add(vm.opaque_ref);

            var (hostIcon, hostTip) = ShellStatusIcons.ForHost(host);
            var isCoordinator = Helpers.HostIsCoordinator(host);
            var hostNode = new InfraTreeNode
            {
                Kind = InfraNodeKind.Host,
                Title = Helpers.GetName(host),
                Subtitle = string.IsNullOrWhiteSpace(host.address) ? host.hostname : host.address,
                Detail = isCoordinator
                    ? $"Coordinator · {hostVms.Count} VM(s)"
                    : $"{hostVms.Count} VM(s)",
                Server = server,
                OpaqueRef = host.opaque_ref,
                IsExpanded = true,
                ShowStatusIcon = true,
                StatusIcon = hostIcon,
                StatusTooltip = hostTip
            };

            foreach (var vm in hostVms)
                hostNode.Children.Add(CreateVmNode(server, vm));

            // Local / single-PBD storage under this host.
            foreach (var sr in allSrs.Where(sr => SameHost(sr.Home(), host)))
            {
                placedSrs.Add(sr.opaque_ref);
                hostNode.Children.Add(CreateSrNode(server, sr));
            }

            root.Children.Add(hostNode);
        }

        var unplaced = vms.Where(vm => !placed.Contains(vm.opaque_ref)).ToList();
        if (unplaced.Count > 0)
        {
            var group = new InfraTreeNode
            {
                Kind = InfraNodeKind.Group,
                Title = "Other VMs",
                Subtitle = "No home host",
                Detail = $"{unplaced.Count} VM(s)",
                Server = server,
                IsExpanded = true
            };

            foreach (var vm in unplaced)
                group.Children.Add(CreateVmNode(server, vm));

            root.Children.Add(group);
        }

        // Shared / pool-level storage under the cluster root.
        foreach (var sr in allSrs.Where(sr => !placedSrs.Contains(sr.opaque_ref)))
            root.Children.Add(CreateSrNode(server, sr));

        return root;
    }

    private static IEnumerable<SR> VisibleSrs(IXenConnection conn) =>
        (conn.Cache.SRs ?? Array.Empty<SR>())
            .Where(sr => sr != null
                         && !sr.IsToolsSR()
                         && sr.Show(true)
                         && sr.HasPBDs())
            .OrderBy(sr => sr.IsLocalSR() ? 0 : 1)
            .ThenBy(sr => sr.NameWithoutHost(), StringComparer.OrdinalIgnoreCase);

    private static InfraTreeNode CreateVmNode(ServerNode server, VM vm)
    {
        var (icon, tip) = ShellStatusIcons.ForVm(vm);
        var home = vm.Home();
        return new InfraTreeNode
        {
            Kind = InfraNodeKind.Vm,
            Title = Helpers.GetName(vm),
            Subtitle = tip,
            Detail = home != null ? $"{tip} · {Helpers.GetName(home)}" : tip,
            Server = server,
            OpaqueRef = vm.opaque_ref,
            IsExpanded = false,
            ShowStatusIcon = true,
            StatusIcon = icon,
            StatusTooltip = tip
        };
    }

    private static InfraTreeNode CreateSrNode(ServerNode server, SR sr)
    {
        var (icon, tip) = ShellStatusIcons.ForSr(sr);
        var free = Util.DiskSizeString(sr.FreeSpace(), 1);
        var total = Util.DiskSizeString(sr.physical_size, 1);
        var kind = sr.IsLocalSR() ? "Local" : "Shared";
        return new InfraTreeNode
        {
            Kind = InfraNodeKind.Storage,
            Title = sr.NameWithoutHost(),
            Subtitle = $"{kind} · {free} free of {total}",
            Detail = tip,
            Server = server,
            OpaqueRef = sr.opaque_ref,
            IsExpanded = false,
            ShowStatusIcon = true,
            StatusIcon = icon,
            StatusTooltip = tip
        };
    }

    private static bool SameHost(Host? a, Host b)
        => a != null && a.opaque_ref == b.opaque_ref;

    public static void ReplaceRoots(ObservableCollection<InfraTreeNode> roots, IEnumerable<InfraTreeNode> next)
    {
        roots.Clear();
        foreach (var node in next)
            roots.Add(node);
    }
}
