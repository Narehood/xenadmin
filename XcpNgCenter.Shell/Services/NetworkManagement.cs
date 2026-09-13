using System.Globalization;
using XenAdmin.Actions;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using Network = XenAPI.Network;

namespace XcpNgCenter.Shell.Services;

/// <summary>Validation and descriptors shared by the editors and the action worker.</summary>
public static class NetworkManagement
{
    public static string? EditNetworkError(Network network)
    {
        if (network.Locked || network.CreateInProgress()) return "This network is busy. Try again when its task finishes.";
        if (network.IsGuestInstallerNetwork() || network.IsMember()) return "This system or bond-member network cannot be edited.";
        return null;
    }

    public static string? TopologyError(Network network)
    {
        if (EditNetworkError(network) is { } error) return error;
        var pifs = Pifs(network);
        if (pifs.Count != network.PIFs.Count) return "Network interface information is incomplete. Refresh and try again.";
        if (pifs.Any(IsProtected)) return "Management, IP-configured, and cluster interfaces cannot be reconfigured here.";
        if (pifs.Any(p => p.Locked)) return "A network interface is busy.";
        if (pifs.Any(p => p.IsPhysical() || p.IsBondNIC() || p.IsTunnelAccessPIF() || p.IsSriovLogicalPIF()))
            return "Physical, bond, tunnel, and SR-IOV network topology cannot be changed here.";
        if (Vifs(network).Any(v => v.currently_attached || v.Locked))
            return "Disconnect attached VM interfaces before changing the VLAN or uplink.";
        return null;
    }

    public static string? RemoveNetworkError(Network network)
    {
        if (TopologyError(network) is { } error) return error;
        if (network.VIFs.Count > 0 || Vifs(network).Count > 0)
            return "Remove or move every VM interface using this network before removing it (including stopped VMs).";
        if (Pifs(network).Any(p => p.VLAN_slave_of.Count > 0 || p.tunnel_transport_PIF_of.Count > 0))
            return "This network is used by another VLAN or tunnel.";
        return null;
    }

    public static string? MtuError(Network network)
    {
        if (EditNetworkError(network) is { } error) return error;
        if (!network.CanUseJumboFrames()) return "MTU cannot be changed on this network.";
        var pifs = Pifs(network);
        if (pifs.Count != network.PIFs.Count || pifs.Any(p => p.Locked || IsProtected(p)))
            return "MTU cannot be changed on management, IP-configured, busy, or unresolved interfaces.";
        if (Vifs(network).Any(v => v.currently_attached || v.Locked))
            return "Disconnect attached VM interfaces before changing the MTU.";
        return null;
    }

    private static bool IsProtected(PIF pif) => pif.management || pif.disallow_unplug
        || pif.ip_configuration_mode is ip_configuration_mode.DHCP or ip_configuration_mode.Static
        || pif.ipv6_configuration_mode is ipv6_configuration_mode.DHCP or ipv6_configuration_mode.Static or ipv6_configuration_mode.Autoconf
        || pif.Connection.Cache.Cluster_hosts.Any(c => c.PIF.opaque_ref == pif.opaque_ref);

    public static List<PIF> Pifs(Network network) => network.Connection.Cache.PIFs
        .Where(p => p.network.opaque_ref == network.opaque_ref).ToList();

    public static List<VIF> Vifs(Network network) => network.Connection.Cache.VIFs
        .Where(v => v.network.opaque_ref == network.opaque_ref).ToList();

    public static IReadOnlyList<PIF> Uplinks(IXenConnection connection)
    {
        var coordinator = Helpers.GetCoordinator(connection);
        return connection.Cache.PIFs.Where(p => coordinator != null && p.host.opaque_ref == coordinator.opaque_ref
            && p.Show(false) && p.IsPhysical() && !p.IsBondMember() && !p.Locked)
            .OrderBy(p => p.device, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? VmError(VM vm)
    {
        if (vm.Locked || vm.current_operations.Count > 0) return "The VM is busy. Try again when its task finishes.";
        if (vm.is_control_domain || vm.is_a_snapshot || vm.is_a_template) return "Select a regular VM to manage its interfaces.";
        if (vm.power_state is not (vm_power_state.Running or vm_power_state.Halted))
            return "The VM must be running or shut down to change its interfaces.";
        return null;
    }

    public static string? VifError(VIF vif, bool toggle = false)
    {
        var vm = vif.Connection.Resolve(vif.VM);
        if (vm == null) return "The VM is no longer available.";
        if (VmError(vm) is { } error) return error;
        if (vif.Locked || vif.current_operations.Count > 0) return "This interface is busy.";
        if (vif.Connection.Resolve(vif.network)?.IsGuestInstallerNetwork() == true) return "This system interface cannot be changed.";
        if (toggle && vm.power_state != vm_power_state.Running) return "Start the VM to connect or disconnect its interface.";
        if (vif.currently_attached && !vif.allowed_operations.Contains(vif_operations.unplug))
            return "This interface cannot be unplugged. Shut down the VM first.";
        if (toggle && !vif.currently_attached && !vif.allowed_operations.Contains(vif_operations.plug))
            return "This interface cannot currently be connected.";
        return null;
    }

    public static IReadOnlyList<Network> VmNetworks(VM vm) => vm.Connection.Cache.Networks
        .Where(n => n.Show(false) && !n.IsGuestInstallerNetwork() && !n.IsMember()
                    && !n.CreateInProgress() && !n.Locked && (!n.IsSriov() || vm.HasSriovRecommendation()))
        .OrderBy(n => n.Name(), StringComparer.OrdinalIgnoreCase).ToList();

    public static NetworkPlan PlanNetwork(IXenConnection connection, NetworkEdit request)
    {
        var current = request.Reference == null ? null : connection.Resolve(new XenRef<Network>(request.Reference))
            ?? throw new InvalidOperationException("The network was removed. Close this editor and refresh.");
        Require(current == null ? null : EditNetworkError(current));
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("Enter a network name.");
        if (!long.TryParse(request.Mtu, NumberStyles.None, CultureInfo.InvariantCulture, out var mtu)
            || (mtu != current?.MTU && (mtu < Network.MTU_MIN || mtu > Network.MTU_MAX)))
            throw new InvalidOperationException($"Enter an MTU between {Network.MTU_MIN} and {Network.MTU_MAX}.");

        var pifs = current == null ? [] : Pifs(current);
        var topologyChanged = current == null || request.External != (pifs.Count > 0)
            || (request.External && pifs.Any(p => p.device != request.Device || p.VLAN.ToString(CultureInfo.InvariantCulture) != request.Vlan));
        // A physical/bond/tunnel editor retains its original topology without offering a VLAN conversion.
        if (request.PreserveTopology) topologyChanged = false;
        if (current != null && topologyChanged) Require(TopologyError(current));
        var mtuChanged = current != null && mtu != current.MTU;
        if (mtuChanged) Require(MtuError(current!));

        PIF? uplink = null;
        long vlan = -1;
        if (request.External && topologyChanged)
        {
            uplink = Uplinks(connection).FirstOrDefault(p => p.device == request.Device)
                ?? throw new InvalidOperationException("Select an available physical interface or bond on the pool coordinator.");
            if (!long.TryParse(request.Vlan, NumberStyles.None, CultureInfo.InvariantCulture, out vlan)
                || vlan < (Helpers.VLAN0Allowed(connection) ? 0 : 1) || vlan > 4094)
                throw new InvalidOperationException($"Enter a VLAN ID between {(Helpers.VLAN0Allowed(connection) ? 0 : 1)} and 4094.");
            if (connection.Cache.PIFs.Any(p => p.device == uplink.device && p.VLAN == vlan
                                             && p.network.opaque_ref != current?.opaque_ref))
                throw new InvalidOperationException($"VLAN {vlan} already exists on {uplink.device} in this pool.");
        }
        var descriptor = current == null ? new Network { Connection = connection } : (Network)current.Clone();
        descriptor.name_label = request.Name.Trim();
        descriptor.name_description = request.Description;
        descriptor.tags = request.Tags.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        descriptor.other_config = new Dictionary<string, string>(descriptor.other_config);
        descriptor.SetAutoPlug(request.Automatic);
        descriptor.MTU = mtu;
        return new NetworkPlan(current, descriptor, topologyChanged, request.External, uplink, vlan, mtuChanged);
    }

    public static VIF PlanVif(VM vm, VifEdit request)
    {
        Require(VmError(vm));
        var current = request.Reference == null ? null : vm.Connection.Resolve(new XenRef<VIF>(request.Reference))
            ?? throw new InvalidOperationException("This interface was removed or replaced. Close the editor and refresh.");
        if (current != null)
        {
            if (current.VM.opaque_ref != vm.opaque_ref) throw new InvalidOperationException("The interface belongs to another VM.");
            Require(VifError(current));
        }
        else if (vm.Connection.Cache.VIFs.Count(v => v.VM.opaque_ref == vm.opaque_ref) >= vm.MaxVIFsAllowed())
            throw new InvalidOperationException("This VM has reached its maximum number of network interfaces.");
        var network = VmNetworks(vm).FirstOrDefault(n => n.opaque_ref == request.NetworkReference)
            ?? throw new InvalidOperationException("Select an available network.");
        var mac = request.Mac.Trim();
        if (mac.Length > 0 && (!Helpers.IsValidMAC(mac) || (Convert.ToByte(mac[..2], 16) & 1) != 0 || mac == "00:00:00:00:00:00"))
            throw new InvalidOperationException("Enter a valid unicast MAC address, such as 02:00:00:00:00:01, or leave it blank to generate one.");
        if (mac.Length > 0 && vm.Connection.Cache.VIFs.Any(v => v.opaque_ref != current?.opaque_ref
            && string.Equals(v.MAC, mac, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("That MAC address is already used by another interface in this pool.");
        if (request.Limit && (!int.TryParse(request.Rate, out var rate) || rate <= 0))
            throw new InvalidOperationException("Enter a positive bandwidth limit in KB/s.");
        var descriptor = current == null ? new VIF { Connection = vm.Connection } : (VIF)current.Clone();
        descriptor.opaque_ref = null;
        descriptor.VM = new XenRef<VM>(vm.opaque_ref);
        descriptor.network = new XenRef<Network>(network.opaque_ref);
        descriptor.device = current?.device ?? NextDevice(vm);
        descriptor.MAC = mac;
        // Generated setters skip equal values, so edit the copy before assigning it.
        var qosParameters = new Dictionary<string, string>(descriptor.qos_algorithm_params);
        if (request.Limit)
        {
            descriptor.qos_algorithm_type = VIF.RATE_LIMIT_QOS_VALUE;
            qosParameters[VIF.KBPS_QOS_PARAMS_KEY] = int.Parse(request.Rate).ToString(CultureInfo.InvariantCulture);
        }
        else if (descriptor.qos_algorithm_type == VIF.RATE_LIMIT_QOS_VALUE)
            descriptor.qos_algorithm_type = "";
        descriptor.qos_algorithm_params = qosParameters;
        return descriptor;
    }

    private static string NextDevice(VM vm)
    {
        var used = vm.Connection.Cache.VIFs.Where(v => v.VM.opaque_ref == vm.opaque_ref).Select(v => v.device).ToHashSet();
        return Enumerable.Range(0, used.Count + 1).First(n => !used.Contains(n.ToString(CultureInfo.InvariantCulture))).ToString(CultureInfo.InvariantCulture);
    }

    public static bool VifSettingsChanged(VIF current, VIF descriptor) =>
        current.network.opaque_ref != descriptor.network.opaque_ref
        || !string.Equals(current.MAC, descriptor.MAC, StringComparison.OrdinalIgnoreCase)
        || current.qos_algorithm_type != descriptor.qos_algorithm_type
        || current.qos_algorithm_params.Count != descriptor.qos_algorithm_params.Count
        || current.qos_algorithm_params.Any(pair => !descriptor.qos_algorithm_params.TryGetValue(pair.Key, out var value) || pair.Value != value);

    public static void Require(string? error)
    {
        if (error != null) throw new InvalidOperationException(error);
    }
}

public sealed record NetworkEdit(string? Reference, string Name, string Description, string Tags, bool Automatic,
    string Mtu, bool External, string? Device, string Vlan, bool PreserveTopology = false);
public sealed record NetworkPlan(Network? Current, Network Descriptor, bool TopologyChanged, bool External,
    PIF? Uplink, long Vlan, bool MtuChanged);
public sealed record VifEdit(string? Reference, string? NetworkReference, string Mac, bool Limit, string Rate);

/// <summary>Build shared actions on the worker after rechecking the current cache, avoiding constructor-time locks in editors.</summary>
public sealed class ShellNetworkAction : AsyncAction
{
    private readonly Action<ShellNetworkAction, Session> _run;
    public bool RebootRequired { get; set; }

    public ShellNetworkAction(IXenConnection connection, string title, Action<ShellNetworkAction, Session> run)
        : base(connection, title) => _run = run;

    protected override void Run()
    {
        if (!Connection.IsConnected) throw new InvalidOperationException("The server disconnected. Reconnect and try again.");
        _run(this, Session);
        Description = RebootRequired ? "Interface saved. Shut down and start the VM to activate it; hot-plug is unavailable."
            : "Network changes completed.";
    }

    public static ShellNetworkAction SaveNetwork(IXenConnection connection, NetworkEdit request) =>
        new(connection, request.Reference == null ? $"Create network {request.Name}" : $"Update network {request.Name}", (_, session) =>
        {
            var plan = NetworkManagement.PlanNetwork(connection, request);
            if (plan.Current == null)
            {
                var create = plan.External ? new NetworkAction(connection, plan.Descriptor, plan.Uplink!, plan.Vlan)
                    : new NetworkAction(connection, plan.Descriptor, true);
                create.RunSync(session);
                return;
            }
            new SaveChangesAction(plan.Descriptor, true, plan.Current).RunSync(session);
            if (plan.TopologyChanged)
                new NetworkAction(connection, plan.Descriptor, true, plan.External, plan.Uplink!, plan.Vlan, true).RunSync(session);
            else if (plan.MtuChanged && NetworkManagement.Pifs(plan.Current).Any(p => p.currently_attached))
                new UnplugPlugNetworkAction(plan.Current, true).RunSync(session);
        });

    public static ShellNetworkAction SaveVif(VM vm, VifEdit request) =>
        new(vm.Connection, request.Reference == null ? "Add VM network interface" : "Edit VM network interface", (action, session) =>
        {
            var currentVm = vm.Connection.Resolve(new XenRef<VM>(vm.opaque_ref))
                ?? throw new InvalidOperationException("The VM is no longer available.");
            var descriptor = NetworkManagement.PlanVif(currentVm, request);
            if (request.Reference == null)
            {
                var create = new CreateVIFAction(currentVm, descriptor, true);
                create.RunSync(session);
                action.RebootRequired = create.RebootRequired;
            }
            else
            {
                var current = vm.Connection.Resolve(new XenRef<VIF>(request.Reference));
                if (!NetworkManagement.VifSettingsChanged(current, descriptor)) return;
                var update = new UpdateVIFAction(currentVm, current, descriptor);
                update.RunSync(session);
                action.RebootRequired = update.RebootRequired;
            }
        });
}
