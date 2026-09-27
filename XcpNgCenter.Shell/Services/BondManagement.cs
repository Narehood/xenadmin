using System.Globalization;
using System.Text.Json;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using Network = XenAPI.Network;

namespace XcpNgCenter.Shell.Services;

public sealed record BondModeOption(string Label, bond_mode Mode, Bond.hashing_algoritm Hashing);
public sealed record BondCreateRequest(string Name, IReadOnlyList<string> MemberReferences, string Mtu,
    BondModeOption Mode, bool Automatic);
public sealed record BondCreatePlan(IReadOnlyList<PIF> CoordinatorMembers, long Mtu, string Snapshot);
public sealed record BondExistingPlan(Network Network, IReadOnlyList<Bond> Bonds, string Snapshot);

/// <summary>Checks every host touched by the shared pool-wide bond actions before any mutation.</summary>
public static class BondManagement
{
    public static IReadOnlyList<BondModeOption> Modes { get; } =
    [
        new("Active-backup", bond_mode.active_backup, Bond.hashing_algoritm.unknown),
        new("Balance SLB", bond_mode.balance_slb, Bond.hashing_algoritm.unknown),
        new("LACP (source MAC)", bond_mode.lacp, Bond.hashing_algoritm.src_mac),
        new("LACP (TCP/UDP ports)", bond_mode.lacp, Bond.hashing_algoritm.tcpudp_ports)
    ];

    public static bool IsBondNetwork(Network network) => NetworkManagement.Pifs(network).Any(p => p.bond_master_of.Count > 0);

    public static IReadOnlyList<PIF> Candidates(IXenConnection connection)
    {
        var coordinator = Helpers.GetCoordinator(connection);
        return connection.Cache.PIFs.Where(p => p.host.opaque_ref == coordinator?.opaque_ref && p.physical && p.VLAN == -1)
            .OrderBy(p => p.device, StringComparer.Ordinal).ToList();
    }

    public static string? CandidateError(PIF pif)
    {
        try
        {
            var hosts = PoolHosts(pif.Connection);
            foreach (var member in MatchingMembers(hosts, [pif.device])) ValidateInterface(member, null);
            return null;
        }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    public static BondCreatePlan PlanCreate(IXenConnection connection, BondCreateRequest request)
    {
        var hosts = PoolHosts(connection);
        ValidateMode(hosts, request.Mode);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("Enter a name for the bond network.");
        var maximum = hosts.All(h => h.vSwitchNetworkBackend()) ? 4 : 2;
        if (request.MemberReferences.Count < 2 || request.MemberReferences.Count > maximum
            || request.MemberReferences.Distinct(StringComparer.Ordinal).Count() != request.MemberReferences.Count)
            throw new InvalidOperationException($"Select between 2 and {maximum} distinct physical NICs.");

        var coordinator = Helpers.GetCoordinator(connection)!;
        var members = request.MemberReferences.Select(reference => connection.Resolve(new XenRef<PIF>(reference))
            ?? throw new InvalidOperationException("A selected NIC was removed. Close the editor and refresh.")).ToList();
        if (members.Any(p => p.host.opaque_ref != coordinator.opaque_ref))
            throw new InvalidOperationException("The pool coordinator changed. Close the editor and refresh.");
        var devices = members.Select(p => p.device).ToArray();
        if (devices.Distinct(StringComparer.Ordinal).Count() != members.Count)
            throw new InvalidOperationException("Select distinct physical devices.");

        var allMembers = MatchingMembers(hosts, devices);
        foreach (var member in allMembers) ValidateInterface(member, null);
        if (!long.TryParse(request.Mtu, NumberStyles.None, CultureInfo.InvariantCulture, out var mtu)
            || mtu < Network.MTU_MIN || mtu > Network.MTU_MAX || allMembers.Any(p => p.MTU < mtu))
            throw new InvalidOperationException($"Enter an MTU between {Network.MTU_MIN} and {Network.MTU_MAX}, no larger than any selected NIC's current MTU.");

        return new(members, mtu, Snapshot(coordinator.opaque_ref, allMembers, []));
    }

    public static BondExistingPlan PlanExisting(IXenConnection connection, string networkReference, BondModeOption? newMode = null)
    {
        var hosts = PoolHosts(connection);
        if (newMode != null) ValidateMode(hosts, newMode);
        var network = connection.Resolve(new XenRef<Network>(networkReference))
            ?? throw new InvalidOperationException("The bond network was removed. Close the editor and refresh.");
        ValidateUnusedNetwork(network);
        var masters = NetworkManagement.Pifs(network);
        if (masters.Count != hosts.Count || network.PIFs.Count != masters.Count
            || network.PIFs.Any(reference => masters.All(p => p.opaque_ref != reference.opaque_ref)))
            throw new InvalidOperationException("Bond interfaces are incomplete across the pool. Refresh and try again.");
        var bonds = new List<Bond>();
        var interfaces = new List<PIF>();
        string[]? expectedDevices = null;
        foreach (var host in hosts)
        {
            var hostMasters = masters.Where(p => p.host.opaque_ref == host.opaque_ref).ToList();
            if (hostMasters.Count != 1 || hostMasters[0].bond_master_of.Count != 1)
                throw new InvalidOperationException("Each host must have exactly one bond on this network.");
            var master = hostMasters[0];
            var bond = connection.Resolve(master.bond_master_of[0]);
            if (bond == null || bond.Locked || bond.master.opaque_ref != master.opaque_ref)
                throw new InvalidOperationException("A bond is busy or its topology is incomplete.");
            ValidateInterface(master, bond, isMaster: true);
            var members = connection.ResolveAll(bond.slaves);
            if (members.Count < 2 || members.Count != bond.slaves.Count
                || members.Select(p => p.opaque_ref).Distinct().Count() != members.Count
                || members.Any(p => p.host.opaque_ref != host.opaque_ref)
                || !members.Any(p => p.opaque_ref == bond.primary_slave.opaque_ref))
                throw new InvalidOperationException("Bond membership or the primary NIC is incomplete. Refresh and try again.");
            var devices = members.Select(p => p.device).Order(StringComparer.Ordinal).ToArray();
            if (devices.Distinct().Count() != devices.Length || expectedDevices != null && !expectedDevices.SequenceEqual(devices))
                throw new InvalidOperationException("Bond members must match across every host in the pool.");
            expectedDevices = devices;
            foreach (var member in members) ValidateInterface(member, bond);
            interfaces.Add(master);
            interfaces.AddRange(members);
            bonds.Add(bond);
        }
        var coordinator = Helpers.GetCoordinator(connection)!;
        var coordinatorBond = bonds.Single(b => connection.Resolve(b.master).host.opaque_ref == coordinator.opaque_ref);
        // DestroyBondAction finds equivalent bonds by device subset. Ensure it will select exactly our validated bonds.
        if (hosts.Any(h => NetworkingHelper.FindBond(h, coordinatorBond)?.opaque_ref != bonds.Single(b => connection.Resolve(b.master).host.opaque_ref == h.opaque_ref).opaque_ref))
            throw new InvalidOperationException("Bond matching is ambiguous. Use the classic client to inspect this topology.");
        bonds.Remove(coordinatorBond);
        bonds.Insert(0, coordinatorBond);
        return new(network, bonds, Snapshot(coordinator.opaque_ref, interfaces, bonds));
    }

    public static string? ExistingError(Network network)
    {
        try { PlanExisting(network.Connection, network.opaque_ref); return null; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    public static void RequireUnchanged(string expected, string current)
    {
        if (!string.Equals(expected, current, StringComparison.Ordinal))
            throw new InvalidOperationException("The pool or bond configuration changed during confirmation. Refresh and review the change again.");
    }

    private static List<Host> PoolHosts(IXenConnection connection)
    {
        var hosts = connection.Cache.Hosts.OrderBy(h => h.opaque_ref, StringComparer.Ordinal).ToList();
        var coordinator = Helpers.GetCoordinator(connection);
        if (hosts.Count == 0 || coordinator == null || hosts.All(h => h.opaque_ref != coordinator.opaque_ref))
            throw new InvalidOperationException("The pool coordinator is unavailable. Refresh and try again.");
        foreach (var host in hosts)
        {
            if (host.Locked || host.current_operations.Count > 0 || !host.enabled
                || connection.Resolve(host.metrics)?.live != true)
                throw new InvalidOperationException($"Host '{host.Name()}' must be online, enabled and idle before changing bonds.");
            if (host.PIFs.Any(p => connection.Resolve(p) == null))
                throw new InvalidOperationException($"Interface information for '{host.Name()}' is incomplete.");
        }
        return hosts;
    }

    private static List<PIF> MatchingMembers(IReadOnlyList<Host> hosts, IReadOnlyList<string> devices)
    {
        var all = new List<PIF>();
        foreach (var host in hosts)
        foreach (var device in devices)
        {
            // Match the same host.PIFs/device lookup used by CreateBondAction, including VLAN children.
            var matches = host.Connection.ResolveAll(host.PIFs).Where(p => p.device == device).ToList();
            if (matches.Count != 1 || matches[0].host.opaque_ref != host.opaque_ref)
                throw new InvalidOperationException($"NIC '{device}' on '{host.Name()}' is missing, has inconsistent host membership, or has dependent interfaces.");
            all.Add(matches[0]);
        }
        return all;
    }

    private static void ValidateMode(IReadOnlyList<Host> hosts, BondModeOption mode)
    {
        if (!Modes.Any(m => m.Mode == mode.Mode && m.Hashing == mode.Hashing))
            throw new InvalidOperationException("Select a supported bond mode.");
        if (mode.Mode == bond_mode.lacp && hosts.Any(h => !h.vSwitchNetworkBackend()))
            throw new InvalidOperationException("LACP requires the vSwitch network backend on every host.");
    }

    private static void ValidateInterface(PIF pif, Bond? owner, bool isMaster = false)
    {
        if (pif.Locked || !pif.managed || pif.VLAN != -1 || !isMaster && !pif.physical)
            throw new InvalidOperationException($"NIC '{pif.device}' is busy or is not an available managed physical interface.");
        if (pif.management || pif.disallow_unplug || pif.ip_configuration_mode != ip_configuration_mode.None
            || pif.ipv6_configuration_mode != ipv6_configuration_mode.None
            || pif.Connection.Cache.Cluster_hosts.Any(c => c.PIF.opaque_ref == pif.opaque_ref))
            throw new InvalidOperationException($"NIC '{pif.device}' carries management, host IP or cluster traffic and cannot be changed here.");
        if (pif.VLAN_slave_of.Count > 0 || pif.tunnel_access_PIF_of.Count > 0 || pif.tunnel_transport_PIF_of.Count > 0
            || pif.sriov_logical_PIF_of.Count > 0 || pif.sriov_physical_PIF_of.Count > 0
            || HasRecord<VLAN>(pif.Connection, v => v.tagged_PIF.opaque_ref == pif.opaque_ref)
            || pif.Connection.Cache.Tunnels.Any(t => t.transport_PIF.opaque_ref == pif.opaque_ref || t.access_PIF.opaque_ref == pif.opaque_ref)
            || HasRecord<Network_sriov>(pif.Connection, s => s.physical_PIF.opaque_ref == pif.opaque_ref || s.logical_PIF.opaque_ref == pif.opaque_ref)
            || pif.Connection.Cache.PIFs.Any(p => p.host.opaque_ref == pif.host.opaque_ref && p.device == pif.device && p.opaque_ref != pif.opaque_ref))
            throw new InvalidOperationException($"NIC '{pif.device}' has VLAN, tunnel, SR-IOV or other dependent interfaces.");
        var hasBondSlaveReference = !string.IsNullOrEmpty(pif.bond_slave_of.opaque_ref) && pif.bond_slave_of.opaque_ref != "OpaqueRef:NULL";
        if (owner == null && (pif.bond_master_of.Count > 0 || hasBondSlaveReference)
            || owner != null && isMaster && hasBondSlaveReference
            || owner != null && !isMaster && (pif.bond_slave_of.opaque_ref != owner.opaque_ref || pif.bond_master_of.Count > 0)
            || pif.Connection.Cache.Bonds.Any(b => b.opaque_ref != owner?.opaque_ref
                && (b.master.opaque_ref == pif.opaque_ref || b.slaves.Any(s => s.opaque_ref == pif.opaque_ref))))
            throw new InvalidOperationException($"NIC '{pif.device}' is already bonded or its bond membership changed.");
        var network = pif.Connection.Resolve(pif.network)
            ?? throw new InvalidOperationException($"The network for NIC '{pif.device}' is unavailable.");
        ValidateUnusedNetwork(network);
    }

    private static bool HasRecord<T>(IXenConnection connection, Predicate<T> predicate) where T : XenObject<T>
    {
        var matches = new List<T>();
        connection.Cache.AddAll(matches, predicate);
        return matches.Count > 0;
    }

    private static void ValidateUnusedNetwork(Network network)
    {
        if (network.Locked || network.CreateInProgress() || network.IsGuestInstallerNetwork())
            throw new InvalidOperationException("A selected network is busy or reserved for the system.");
        if (network.VIFs.Count > 0 || NetworkManagement.Vifs(network).Count > 0)
            throw new InvalidOperationException("Move or remove every VM interface from the affected networks first, including stopped VMs.");
    }

    private static string Snapshot(string coordinator, IEnumerable<PIF> pifs, IEnumerable<Bond> bonds) => JsonSerializer.Serialize(new
    {
        Coordinator = coordinator,
        Interfaces = pifs.OrderBy(p => p.opaque_ref, StringComparer.Ordinal).Select(p => new
        {
            p.opaque_ref, p.uuid, Host = p.host.opaque_ref, HostUuid = p.Connection.Resolve(p.host)?.uuid,
            p.device, Network = p.network.opaque_ref, NetworkUuid = p.Connection.Resolve(p.network)?.uuid,
            p.MTU, NetworkName = p.Connection.Resolve(p.network)?.name_label
        }),
        Bonds = bonds.OrderBy(b => b.opaque_ref, StringComparer.Ordinal).Select(b => new
        {
            b.opaque_ref, b.uuid, b.mode, Primary = b.primary_slave.opaque_ref,
            Members = b.slaves.Select(p => p.opaque_ref).Order(StringComparer.Ordinal),
            Properties = b.properties.OrderBy(p => p.Key, StringComparer.Ordinal)
        })
    });
}
