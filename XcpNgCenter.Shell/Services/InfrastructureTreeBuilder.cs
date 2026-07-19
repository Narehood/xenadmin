using System.Collections.ObjectModel;
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

        var root = new InfraTreeNode
        {
            Kind = InfraNodeKind.Pool,
            Title = poolName,
            Subtitle = conn.HostnameWithPort,
            Detail = $"{hosts.Count} host(s), {vms.Count} VM(s)",
            Server = server,
            OpaqueRef = pool?.opaque_ref,
            IsExpanded = true
        };

        var placed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var host in hosts)
        {
            var hostVms = vms
                .Where(vm => SameHost(vm.Home(), host))
                .ToList();

            foreach (var vm in hostVms)
                placed.Add(vm.opaque_ref);

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
                IsExpanded = true
            };

            foreach (var vm in hostVms)
                hostNode.Children.Add(CreateVmNode(server, vm));

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

        return root;
    }

    private static InfraTreeNode CreateVmNode(ServerNode server, VM vm)
    {
        var power = vm.power_state.ToString();
        var home = vm.Home();
        return new InfraTreeNode
        {
            Kind = InfraNodeKind.Vm,
            Title = Helpers.GetName(vm),
            Subtitle = power,
            Detail = home != null
                ? $"{power} · {Helpers.GetName(home)}"
                : power,
            Server = server,
            OpaqueRef = vm.opaque_ref,
            IsExpanded = false
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
