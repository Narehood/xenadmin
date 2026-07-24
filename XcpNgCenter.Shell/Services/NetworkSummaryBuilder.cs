using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Read-only network summary for the Avalonia shell (networks / management PIFs / VIFs).
/// </summary>
public static class NetworkSummaryBuilder
{
    private const bool ShowHidden = false;

    public readonly record struct NetworkSummary(
        IReadOnlyList<GeneralPropertyRow> Totals,
        IReadOnlyList<NetworkItemRow> Networks,
        IReadOnlyList<NetworkItemRow> Management);

    public static NetworkSummary Build(InfraTreeNode? node)
    {
        if (node?.Server?.Connection is not { IsConnected: true } conn)
            return Empty;

        return node.Kind switch
        {
            InfraNodeKind.Pool => BuildPoolOrHost(conn, host: null),
            InfraNodeKind.Host => BuildPoolOrHost(conn, FindHost(conn, node.OpaqueRef)),
            InfraNodeKind.Vm => BuildVm(conn, FindVm(conn, node.OpaqueRef)),
            _ => Empty
        };
    }

    private static NetworkSummary Empty { get; } = new(
        Array.Empty<GeneralPropertyRow>(),
        Array.Empty<NetworkItemRow>(),
        Array.Empty<NetworkItemRow>());

    private static NetworkSummary BuildPoolOrHost(IXenConnection conn, Host? host)
    {
        var networks = (conn.Cache.Networks ?? Array.Empty<XenAPI.Network>())
            .Where(n => n != null && n.Show(ShowHidden))
            .OrderBy(n => Helpers.GetName(n), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var networkRows = networks.Select(network =>
        {
            var pif = Helpers.FindPIF(network, host);
            var nic = pif != null ? Helpers.GetName(pif) : "—";
            var vlan = Helpers.VlanString(pif);
            var link = host == null
                ? network.LinkStatusString()
                : pif?.LinkStatusString() ?? "—";
            var auto = network.GetAutoPlug() ? "Auto" : "Manual";
            var mtu = network.CanUseJumboFrames() ? network.MTU.ToString() : "—";
            var mac = pif != null && pif.IsPhysical() && !string.IsNullOrWhiteSpace(pif.MAC)
                ? Helpers.GetMacString(pif.MAC)
                : "—";

            return new NetworkItemRow(
                Helpers.GetName(network),
                $"{nic} · VLAN {vlan}",
                $"{link} · {auto}",
                mtu == "—" ? "MTU —" : $"MTU {mtu}",
                mac);
        }).ToList();

        var mgmtRows = CollectManagementPifs(conn, host)
            .Select(pif =>
            {
                var pifHost = conn.Resolve(pif.host);
                var network = conn.Resolve(pif.network);
                var purpose = pif.management ? "Management" : (pif.GetManagementPurpose() ?? "Secondary");
                return new NetworkItemRow(
                    pifHost != null
                        ? $"{IdentifierPrivacy.ServerName(Helpers.GetName(pifHost))} · {purpose}"
                        : purpose,
                    $"{(network != null ? Helpers.GetName(network) : "—")} · {Helpers.GetName(pif)}",
                    pif.IpConfigurationModeString(),
                    IdentifierPrivacy.Address(string.IsNullOrWhiteSpace(pif.IP) ? "—" : pif.IP),
                    IdentifierPrivacy.Address(string.IsNullOrWhiteSpace(pif.DNS) ? "—" : pif.DNS));
            })
            .ToList();

        var totals = new List<GeneralPropertyRow>
        {
            new("Networks", networkRows.Count.ToString()),
            new("Management interfaces", mgmtRows.Count.ToString())
        };

        return new NetworkSummary(totals, networkRows, mgmtRows);
    }

    private static NetworkSummary BuildVm(IXenConnection conn, VM? vm)
    {
        if (vm == null)
            return Empty;

        var vifs = conn.ResolveAll(vm.VIFs)
            .Where(vif => vif != null)
            .Where(vif =>
            {
                var network = conn.Resolve(vif.network);
                return network == null || !network.IsGuestInstallerNetwork() || ShowHidden;
            })
            .OrderBy(vif =>
            {
                _ = int.TryParse(vif.device, out var n);
                return n;
            })
            .ThenBy(vif => vif.device, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = vifs.Select(vif =>
        {
            var attached = vif.currently_attached ? "Attached" : "Detached";
            var limit = string.IsNullOrEmpty(vif.qos_algorithm_type)
                ? "No limit"
                : vif.LimitString();
            var ip = vif.IPAddressesAsString();
            if (string.IsNullOrWhiteSpace(ip))
                ip = "—";
            else
                ip = IdentifierPrivacy.Address(ip);

            return new NetworkItemRow(
                string.IsNullOrWhiteSpace(vif.device) ? "VIF" : $"Device {vif.device}",
                vif.NetworkName(),
                attached,
                ip,
                $"{Helpers.GetMacString(vif.MAC)} · {limit}");
        }).ToList();

        var totals = new List<GeneralPropertyRow>
        {
            new("VIFs", rows.Count.ToString())
        };

        return new NetworkSummary(totals, rows, Array.Empty<NetworkItemRow>());
    }

    private static List<PIF> CollectManagementPifs(IXenConnection conn, Host? host)
    {
        var hosts = host != null
            ? new List<Host> { host }
            : (conn.Cache.Hosts ?? Array.Empty<Host>())
                .OrderBy(h => Helpers.HostIsCoordinator(h) ? 0 : 1)
                .ThenBy(h => Helpers.GetName(h), StringComparer.OrdinalIgnoreCase)
                .ToList();

        var rows = new List<PIF>();
        foreach (var h in hosts)
        {
            foreach (var pif in conn.Cache.PIFs ?? Array.Empty<PIF>())
            {
                if (pif == null || pif.host != h.opaque_ref)
                    continue;
                if (!pif.IsManagementInterface(ShowHidden))
                    continue;
                rows.Add(pif);
            }
        }

        return rows;
    }

    private static Host? FindHost(IXenConnection conn, string? opaqueRef)
        => string.IsNullOrEmpty(opaqueRef)
            ? null
            : conn.Cache.Hosts?.FirstOrDefault(h => h.opaque_ref == opaqueRef);

    private static VM? FindVm(IXenConnection conn, string? opaqueRef)
        => string.IsNullOrEmpty(opaqueRef)
            ? null
            : conn.Cache.VMs?.FirstOrDefault(v => v.opaque_ref == opaqueRef);
}
