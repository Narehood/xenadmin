using XenAdmin.Actions;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using Network = XenAPI.Network;

namespace XcpNgCenter.Shell.Services;

public sealed record SriovPifIdentity(string Reference, string Uuid, string HostReference, string Device,
    string Mac, string NetworkReference, string VendorId, string DeviceId, string HostUuid, string NetworkUuid);
public sealed record SriovUplinkOption(string Device, string Label, IReadOnlyList<SriovPifIdentity> Targets, string? Error);
public sealed record SriovNetworkRequest(string Name, string Description, bool Automatic, IReadOnlyList<SriovPifIdentity> Targets);
public sealed record SriovNetworkPlan(Network Descriptor, IReadOnlyList<PIF> Pifs);
public sealed record SriovRemovalTarget(string LogicalReference, string LogicalUuid, string SriovReference, string SriovUuid, SriovPifIdentity Physical);
public sealed record SriovRemovalRequest(string NetworkReference, string NetworkUuid, IReadOnlyList<SriovRemovalTarget> Targets);

/// <summary>Plans pool-wide SR-IOV provisioning without changing the cached interfaces.</summary>
public static class SriovNetworkManagement
{
    public static IReadOnlyList<SriovUplinkOption> Uplinks(IXenConnection connection)
    {
        var coordinator = Helpers.GetCoordinator(connection);
        if (coordinator == null) return [];
        return connection.Cache.PIFs
            .Where(p => p.host.opaque_ref == coordinator.opaque_ref && p.physical && p.SriovCapable())
            .OrderBy(p => p.device, StringComparer.Ordinal)
            .Select(p => Option(connection, p.device)).ToList();
    }

    private static SriovUplinkOption Option(IXenConnection connection, string device)
    {
        var pifs = connection.Cache.PIFs.Where(p => p.physical && p.device == device).ToList();
        var identities = pifs.Select(p => Identity(connection, p)).ToArray();
        string? error = null;
        try { Plan(connection, new SriovNetworkRequest("SR-IOV", "", false, identities)); }
        catch (InvalidOperationException exception) { error = exception.Message; }
        return new(device, $"{device} · {pifs.Count} host(s)", identities, error);
    }

    private static SriovPifIdentity Identity(IXenConnection connection, PIF pif)
    {
        var metrics = connection.Resolve(pif.metrics);
        return new(pif.opaque_ref, pif.uuid, pif.host.opaque_ref, pif.device, pif.MAC, pif.network.opaque_ref,
            metrics?.vendor_id ?? "", metrics?.device_id ?? "", connection.Resolve(pif.host)?.uuid ?? "", connection.Resolve(pif.network)?.uuid ?? "");
    }

    public static SriovNetworkPlan Plan(IXenConnection connection, SriovNetworkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("Enter a network name.");
        var hosts = connection.Cache.Hosts;
        var coordinator = Helpers.GetCoordinator(connection);
        if (hosts.Length == 0 || coordinator == null) throw new InvalidOperationException("Pool information is incomplete. Reconnect and refresh.");
        if (hosts.Any(h => !Helpers.KolkataOrGreater(h) || Host.RestrictSriovNetwork(h) || Host.SriovNetworkDisabled(h)))
            throw new InvalidOperationException("SR-IOV networking is unsupported or restricted on a host in this pool.");
        if (request.Targets.Count != hosts.Length
            || request.Targets.Select(p => p.Reference).Distinct(StringComparer.Ordinal).Count() != hosts.Length
            || !hosts.Select(h => h.opaque_ref).ToHashSet(StringComparer.Ordinal)
                .SetEquals(request.Targets.Select(p => p.HostReference)))
            throw new InvalidOperationException("Select one physical NIC on every current pool host. Refresh after any pool membership change.");
        if (request.Targets.Select(p => p.Device).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidOperationException("Use the same physical NIC device on every host.");
        if (request.Targets.Any(p => !HasCompleteIdentity(p)))
            throw new InvalidOperationException("Host, NIC, or network identity is incomplete. Refresh the pool before provisioning SR-IOV.");
        if (request.Targets.Any(p => string.IsNullOrWhiteSpace(p.VendorId) || string.IsNullOrWhiteSpace(p.DeviceId))
            || request.Targets.Select(p => (p.VendorId, p.DeviceId)).Distinct().Count() != 1)
            throw new InvalidOperationException("Every host must report the same NIC vendor and device model. Refresh missing hardware information.");

        var pifs = new List<PIF>();
        foreach (var expected in request.Targets)
        {
            var pif = connection.Resolve(new XenRef<PIF>(expected.Reference))
                ?? throw new InvalidOperationException("A selected NIC was removed. Refresh the interface list.");
            if (Identity(connection, pif) != expected)
                throw new InvalidOperationException("A selected NIC or its network changed. Refresh and review the interfaces again.");
            var host = connection.Resolve(pif.host)
                ?? throw new InvalidOperationException("A selected host was removed. Refresh the pool.");
            if (host.Locked || host.current_operations.Count > 0 || connection.Resolve(host.metrics)?.live != true)
                throw new InvalidOperationException($"Host '{host.Name()}' is offline, busy, or has incomplete health information.");
            NetworkManagement.Require(InterfaceError(connection, pif));
            pifs.Add(pif);
        }
        var network = new Network
        {
            Connection = connection, name_label = request.Name.Trim(), name_description = request.Description,
            MTU = Network.MTU_DEFAULT
        };
        network.SetAutoPlug(request.Automatic);
        return new(network, pifs.OrderBy(p => p.host.opaque_ref == coordinator.opaque_ref ? 0 : 1)
            .ThenBy(p => p.host.opaque_ref, StringComparer.Ordinal).ToArray());
    }

    public static string? RemovalError(Network network)
    {
        try { CaptureRemoval(network); return null; }
        catch (InvalidOperationException exception) { return exception.Message; }
    }

    public static SriovRemovalRequest CaptureRemoval(Network network)
    {
        var connection = network.Connection;
        var targets = NetworkManagement.Pifs(network).Select(p =>
        {
            if (p.sriov_logical_PIF_of.Count != 1)
                throw new InvalidOperationException("This is not a complete SR-IOV logical network.");
            var sriov = connection.Resolve(p.sriov_logical_PIF_of[0])
                ?? throw new InvalidOperationException("SR-IOV information is incomplete. Refresh the pool.");
            var physical = connection.Resolve(sriov.physical_PIF)
                ?? throw new InvalidOperationException("The physical SR-IOV NIC is missing. Refresh the pool.");
            return new SriovRemovalTarget(p.opaque_ref, p.uuid, sriov.opaque_ref, sriov.uuid, Identity(connection, physical));
        }).ToArray();
        var request = new SriovRemovalRequest(network.opaque_ref, network.uuid, targets);
        PlanRemoval(connection, request);
        return request;
    }

    public static Network PlanRemoval(IXenConnection connection, SriovRemovalRequest request)
    {
        var network = connection.Resolve(new XenRef<Network>(request.NetworkReference))
            ?? throw new InvalidOperationException("The network was removed. Refresh the pool.");
        if (string.IsNullOrWhiteSpace(request.NetworkUuid) || network.uuid != request.NetworkUuid)
            throw new InvalidOperationException("The network identity is incomplete or was replaced. Review the current network.");
        NetworkManagement.Require(NetworkManagement.EditNetworkError(network));
        if (network.VIFs.Count > 0 || NetworkManagement.Vifs(network).Count > 0)
            throw new InvalidOperationException("Remove or move all VM interfaces before removing this SR-IOV network, including stopped VMs.");
        var pifs = NetworkManagement.Pifs(network);
        var expectedRefs = request.Targets.Select(t => t.LogicalReference).ToHashSet(StringComparer.Ordinal);
        if (pifs.Count == 0 || pifs.Count != request.Targets.Count || expectedRefs.Count != request.Targets.Count
            || !expectedRefs.SetEquals(pifs.Select(p => p.opaque_ref))
            || network.PIFs.Count != pifs.Count || !expectedRefs.SetEquals(network.PIFs.Select(p => p.opaque_ref)))
            throw new InvalidOperationException("The SR-IOV network topology changed or is incomplete. Refresh before removing it.");
        foreach (var target in request.Targets)
        {
            var logical = connection.Resolve(new XenRef<PIF>(target.LogicalReference))!;
            var sriov = connection.Resolve(new XenRef<Network_sriov>(target.SriovReference));
            var physical = connection.Resolve(new XenRef<PIF>(target.Physical.Reference));
            if (string.IsNullOrWhiteSpace(target.LogicalUuid) || string.IsNullOrWhiteSpace(target.SriovUuid) || !HasCompleteIdentity(target.Physical)
                || logical.uuid != target.LogicalUuid || logical.physical || logical.VLAN != -1
                || logical.sriov_logical_PIF_of.Count != 1 || logical.sriov_logical_PIF_of[0].opaque_ref != target.SriovReference
                || sriov == null || sriov.uuid != target.SriovUuid || sriov.logical_PIF.opaque_ref != logical.opaque_ref || physical == null
                || sriov.physical_PIF.opaque_ref != physical.opaque_ref || Identity(connection, physical) != target.Physical
                || logical.host.opaque_ref != physical.host.opaque_ref
                || physical.sriov_physical_PIF_of.Count != 1 || physical.sriov_physical_PIF_of[0].opaque_ref != sriov.opaque_ref)
                throw new InvalidOperationException("An SR-IOV interface was changed or replaced. Review the current topology.");
            if (sriov.requires_reboot) throw new InvalidOperationException("A host restart is pending for this SR-IOV network. Complete it and refresh before removal.");
            var host = connection.Resolve(logical.host);
            if (host == null || host.Locked || host.current_operations.Count > 0 || connection.Resolve(host.metrics)?.live != true)
                throw new InvalidOperationException("All affected hosts must be online and idle before removing SR-IOV.");
            if (logical.Locked || IsProtected(connection, logical) || HasDependents(connection, logical)
                || HasBondDependency(connection, logical) || logical.IsTunnelAccessPIF())
                throw new InvalidOperationException("A logical SR-IOV interface is busy, protected, or has dependent networks.");
            NetworkManagement.Require(InterfaceError(connection, physical, removingExisting: true));
        }
        return network;
    }

    private static bool IsProtected(IXenConnection connection, PIF pif) => pif.management || pif.disallow_unplug
        || pif.ip_configuration_mode != ip_configuration_mode.None
        || pif.ipv6_configuration_mode != ipv6_configuration_mode.None
        || connection.Cache.Cluster_hosts.Any(c => c.PIF.opaque_ref == pif.opaque_ref);

    private static bool HasDependents(IXenConnection connection, PIF pif) => pif.VLAN_slave_of.Count > 0
        || pif.tunnel_transport_PIF_of.Count > 0
        || Any<VLAN>(connection, v => v.tagged_PIF.opaque_ref == pif.opaque_ref)
        || connection.Cache.Tunnels.Any(t => t.transport_PIF.opaque_ref == pif.opaque_ref);

    private static string? InterfaceError(IXenConnection connection, PIF pif, bool removingExisting = false)
    {
        if (!pif.physical || !pif.managed || !pif.IsPhysical() || !pif.SriovCapable())
            return "Select a managed physical NIC that reports SR-IOV support.";
        if (pif.Locked) return "A selected NIC is busy.";
        if (IsProtected(connection, pif))
            return "Management, IP-configured, protected, and cluster interfaces cannot be used for SR-IOV provisioning.";
        if (HasBondDependency(connection, pif))
            return "Bond interfaces and bond members cannot be used for SR-IOV provisioning.";
        if (!removingExisting && (pif.IsSriovPhysicalPIF() || pif.IsSriovLogicalPIF()
            || Any<Network_sriov>(connection, s => s.physical_PIF.opaque_ref == pif.opaque_ref || s.logical_PIF.opaque_ref == pif.opaque_ref)))
            return "SR-IOV is already configured on a selected NIC. Existing SR-IOV networks are not replaced.";
        if (HasDependents(connection, pif))
            return "Remove dependent VLANs or tunnels before provisioning SR-IOV on this NIC.";
        var network = connection.Resolve(pif.network);
        if (network == null || network.Locked || network.CreateInProgress() || network.IsGuestInstallerNetwork())
            return "The physical NIC network is missing, busy, or reserved.";
        if (network.VIFs.Count > 0 || connection.Cache.VIFs.Any(v => v.network.opaque_ref == network.opaque_ref))
            return "Move every VM interface off the physical NIC network first, including interfaces on stopped VMs.";
        return null;
    }

    private static bool HasCompleteIdentity(SriovPifIdentity identity) => !string.IsNullOrWhiteSpace(identity.Uuid)
        && !string.IsNullOrWhiteSpace(identity.HostUuid) && !string.IsNullOrWhiteSpace(identity.NetworkUuid);

    // IsBondMember() only sees a bond after that object has arrived in the cache.
    // A raw forward reference is already enough to reject an unsafe operation.
    private static bool HasBondDependency(IXenConnection connection, PIF pif) => pif.bond_master_of.Count > 0
        || !string.IsNullOrEmpty(pif.bond_slave_of.opaque_ref) && pif.bond_slave_of.opaque_ref != "OpaqueRef:NULL"
        || connection.Cache.Bonds.Any(b => b.master.opaque_ref == pif.opaque_ref || b.slaves.Any(s => s.opaque_ref == pif.opaque_ref));

    private static bool Any<T>(IXenConnection connection, Predicate<T> predicate) where T : XenObject<T>
    {
        var matches = new List<T>();
        connection.Cache.AddAll(matches, predicate);
        return matches.Count > 0;
    }
}

public sealed class RemoveShellSriovNetworkAction : AsyncAction
{
    private readonly SriovRemovalRequest _request;

    public RemoveShellSriovNetworkAction(IXenConnection connection, SriovRemovalRequest request)
        : base(connection, "Remove SR-IOV network")
    {
        _request = request with { Targets = request.Targets.ToArray() };
        ApiMethodsToRoleCheck.AddRange("network.destroy", "network_sriov.destroy");
    }

    protected override void Run()
    {
        if (!Connection.IsConnected) throw new InvalidOperationException("The server disconnected. Reconnect and try again.");
        var network = SriovNetworkManagement.PlanRemoval(Connection, _request);
        // Only the validated logical PIFs reach the shared destroy action. A
        // physical PIF here would invoke PIF.forget, so never use a broad filter.
        new NetworkAction(Connection, network, false).RunSync(Session);
        Description = "SR-IOV network removed. Some NIC drivers require a planned host restart after disabling SR-IOV; no hosts were restarted.";
    }
}

public sealed class CreateShellSriovNetworkAction : AsyncAction
{
    private readonly SriovNetworkRequest _request;
    public string CompletionNotice { get; private set; } = "";

    public CreateShellSriovNetworkAction(IXenConnection connection, SriovNetworkRequest request)
        : base(connection, $"Create SR-IOV network {request.Name}")
    {
        _request = request with { Targets = request.Targets.ToArray() };
        ApiMethodsToRoleCheck.AddRange("Network.create", "Network_sriov.async_create");
    }

    protected override void Run()
    {
        if (!Connection.IsConnected) throw new InvalidOperationException("The server disconnected. Reconnect and try again.");
        // The request retains exact NIC identities and all pool members reviewed
        // by the user. Do not expand it to newly joined/replaced hosts here.
        var plan = SriovNetworkManagement.Plan(Connection, _request);
        new CreateSriovAction(Connection, plan.Descriptor, plan.Pifs.ToList()).RunSync(Session);
        CompletionNotice = "SR-IOV network created. Refresh the pool to check whether any hosts require a restart before use.";
        try
        {
            var physicalRefs = plan.Pifs.Select(p => p.opaque_ref).ToHashSet(StringComparer.Ordinal);
            var records = Network_sriov.get_all_records(Session).Values
                .Where(s => physicalRefs.Contains(s.physical_PIF.opaque_ref)).ToArray();
            if (records.Length == physicalRefs.Count)
            {
                var restarts = records.Count(s => s.requires_reboot);
                CompletionNotice = restarts > 0
                    ? $"SR-IOV network created. {restarts} host(s) require a planned restart before use; no hosts were restarted."
                    : "SR-IOV network created. The server reports no host restart requirement. Shut down a VM before assigning its SR-IOV interface.";
            }
        }
        catch
        {
            // Provisioning succeeded. An unavailable status read must not invite
            // users to retry the mutation or claim an automatic rollback.
        }
        Description = CompletionNotice;
    }
}
