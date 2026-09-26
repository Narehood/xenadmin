using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using XenAdmin.Actions;
using XenAdmin.Network;
using XenAPI;

namespace XcpNgCenter.Shell.Services;

public enum HostIpFamily { IPv4, IPv6 }
public enum HostIpMode { None, DHCP, Static, Autoconf }

public sealed record HostIpSnapshot(string HostReference, string HostUuid, string PifReference, string PifUuid,
    string NetworkReference, string NetworkUuid, string Configuration);
public sealed record HostIpEdit(HostIpSnapshot Original, HostIpFamily Family, HostIpMode Mode,
    string Address, string Netmask, string Gateway, string Dns);
public sealed record HostIpPlan(Host Host, PIF Current, PIF Descriptor, HostIpFamily Family,
    bool Changed, bool ManagementAddressChanged, string ReconnectNotice);

/// <summary>Plans one address-family change on one existing host interface.</summary>
public static class HostIpManagement
{
    public static string? HostError(Host host)
    {
        if (string.IsNullOrWhiteSpace(host.uuid)) return "Host identity is incomplete. Refresh and try again.";
        if (host.Locked || host.current_operations.Count > 0) return "The host is busy. Wait for its current task to finish.";
        if (host.Connection.Resolve(host.metrics) is not { live: true }) return "Host status is unavailable or the host is offline.";
        if (host.Connection.Cache.Pools.Any(p => p.ha_enabled)) return "Disable pool HA before changing host IP configuration.";
        return null;
    }

    public static string? InterfaceError(PIF pif)
    {
        var host = pif.Connection.Resolve(pif.host);
        if (host == null) return "The host is no longer available.";
        if (HostError(host) is { } error) return error;
        if (string.IsNullOrWhiteSpace(pif.uuid)) return "Interface identity is incomplete. Refresh and try again.";
        if (!pif.managed || pif.Locked) return "The interface is unmanaged or busy.";
        if (!pif.currently_attached) return "Connect the host interface before configuring its IP address.";
        if (pif.disallow_unplug || pif.IsUsedByClustering()) return "Protected and cluster interfaces cannot be reconfigured here.";
        if (!IsNullReference(pif.bond_slave_of.opaque_ref) || pif.tunnel_access_PIF_of.Count > 0
            || pif.tunnel_transport_PIF_of.Count > 0 || pif.IsSriovLogicalPIF() || pif.IsSriovPhysicalPIF())
            return "Bond members, tunnel interfaces, and SR-IOV interfaces cannot be reconfigured here.";
        var network = pif.Connection.Resolve(pif.network);
        if (network == null || string.IsNullOrWhiteSpace(network.uuid)) return "Network information is incomplete. Refresh and try again.";
        return NetworkManagement.EditNetworkError(network);
    }

    public static IReadOnlyList<PIF> Interfaces(Host host) => host.Connection.Cache.PIFs
        .Where(p => p.host.opaque_ref == host.opaque_ref && p.Show(false))
        .OrderByDescending(p => p.management).ThenBy(p => p.device, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.VLAN).ToList();

    public static bool SupportsIpv6(Host host) => host.software_version.TryGetValue("product_version", out var text)
        && Version.TryParse(text, out var version) && version >= new Version(6, 1);

    public static HostIpSnapshot Capture(Host host, PIF pif)
    {
        if (pif.host.opaque_ref != host.opaque_ref || !ReferenceEquals(host.Connection, pif.Connection))
            throw new InvalidOperationException("The interface belongs to another host.");
        var network = pif.Connection.Resolve(pif.network)
            ?? throw new InvalidOperationException("Network information is incomplete.");
        return new(host.opaque_ref, host.uuid, pif.opaque_ref, pif.uuid, network.opaque_ref, network.uuid, Configuration(pif));
    }

    public static HostIpPlan Plan(IXenConnection connection, HostIpEdit request)
    {
        var original = request.Original;
        var host = connection.Resolve(new XenRef<Host>(original.HostReference));
        var pif = connection.Resolve(new XenRef<PIF>(original.PifReference));
        var network = connection.Resolve(new XenRef<XenAPI.Network>(original.NetworkReference));
        if (host == null || pif == null || network == null || host.uuid != original.HostUuid || pif.uuid != original.PifUuid
            || network.uuid != original.NetworkUuid || pif.host.opaque_ref != host.opaque_ref || pif.network.opaque_ref != network.opaque_ref)
            throw new InvalidOperationException("The host interface was removed or replaced. Close this editor and refresh.");
        NetworkManagement.Require(InterfaceError(pif));
        if (Configuration(pif) != original.Configuration)
            throw new InvalidOperationException("The interface configuration changed while this editor was open. Close it and review the current settings.");
        if (!Enum.IsDefined(request.Family) || !Enum.IsDefined(request.Mode)
            || request.Family == HostIpFamily.IPv4 && request.Mode == HostIpMode.Autoconf)
            throw new InvalidOperationException("Select a supported IP configuration mode.");
        if (request.Family == HostIpFamily.IPv6 && !SupportsIpv6(host))
            throw new InvalidOperationException("IPv6 configuration requires a server reporting version 6.1 or newer.");
        if (request.Family == HostIpFamily.IPv4)
        {
            // ChangeNetworkingAction selects through network.PIFs rather than
            // the reviewed opaque_ref. Its host filter must resolve exactly the
            // selected PIF, otherwise it can skip or expand this operation.
            var linked = connection.ResolveAll(network.PIFs);
            var sameHost = linked.Where(p => p.host.opaque_ref == host.opaque_ref).ToArray();
            var inverse = connection.Cache.PIFs.Where(p => p.network.opaque_ref == network.opaque_ref && p.host.opaque_ref == host.opaque_ref).ToArray();
            if (linked.Count != network.PIFs.Count || network.PIFs.Select(p => p.opaque_ref).Distinct().Count() != network.PIFs.Count
                || linked.Any(p => p.network.opaque_ref != network.opaque_ref)
                || sameHost.Length != 1 || sameHost[0].opaque_ref != pif.opaque_ref
                || inverse.Length != 1 || inverse[0].opaque_ref != pif.opaque_ref)
                throw new InvalidOperationException("The network interface inventory is incomplete or ambiguous. Refresh before changing this host's IPv4 configuration.");
        }
        var primary = pif.management && (request.Family == HostIpFamily.IPv4
            ? pif.primary_address_type == primary_address_type.IPv4 : pif.primary_address_type == primary_address_type.IPv6);
        if (primary && request.Mode == HostIpMode.None)
            throw new InvalidOperationException("The primary management address family cannot be disabled.");

        var descriptor = (PIF)pif.Clone();
        var family = request.Family == HostIpFamily.IPv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        var dns = DnsForFamily(pif.DNS, family);
        if (request.Mode == HostIpMode.Static) dns = ParseDns(request.Dns, family);
        var previousDns = DnsForFamily(pif.DNS, family);
        bool addressChanged;
        if (request.Family == HostIpFamily.IPv4)
        {
            descriptor.ip_configuration_mode = request.Mode switch
            {
                HostIpMode.DHCP => ip_configuration_mode.DHCP,
                HostIpMode.Static => ip_configuration_mode.Static,
                _ => ip_configuration_mode.None
            };
            if (request.Mode == HostIpMode.Static)
            {
                var address = ParseAddress(request.Address, family, "IPv4 address");
                var mask = ParseMask(request.Netmask);
                var bits = ToUInt32(address);
                var hostBits = ~mask;
                if (hostBits > 1 && ((bits & hostBits) == 0 || (bits & hostBits) == hostBits))
                    throw new InvalidOperationException("The IPv4 address must not be the subnet's network or broadcast address.");
                descriptor.IP = address.ToString();
                descriptor.netmask = new IPAddress(new[] { (byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask }).ToString();
                descriptor.gateway = string.IsNullOrWhiteSpace(request.Gateway) ? "" : ParseAddress(request.Gateway, family, "IPv4 gateway").ToString();
                if (descriptor.gateway.Length > 0)
                {
                    var gateway = ToUInt32(IPAddress.Parse(descriptor.gateway));
                    if ((gateway & mask) != (bits & mask))
                        throw new InvalidOperationException("The IPv4 gateway must be in the interface's subnet.");
                    if (gateway == bits || hostBits > 1 && ((gateway & hostBits) == 0 || (gateway & hostBits) == hostBits))
                        throw new InvalidOperationException("The IPv4 gateway must be another host address, not the interface, network, or broadcast address.");
                }
                if (connection.Cache.PIFs.Any(p => p.opaque_ref != pif.opaque_ref && IPAddress.TryParse(p.IP, out var existing) && existing.Equals(address)))
                    throw new InvalidOperationException("That IPv4 address is already assigned to another interface in this pool.");
            }
            else if (request.Mode == HostIpMode.None)
            {
                descriptor.IP = descriptor.netmask = descriptor.gateway = "";
            }
            addressChanged = descriptor.ip_configuration_mode != pif.ip_configuration_mode || request.Mode == HostIpMode.Static
                && (descriptor.IP != pif.IP || descriptor.netmask != pif.netmask || descriptor.gateway != pif.gateway);
        }
        else
        {
            descriptor.ipv6_configuration_mode = request.Mode switch
            {
                HostIpMode.DHCP => ipv6_configuration_mode.DHCP,
                HostIpMode.Static => ipv6_configuration_mode.Static,
                HostIpMode.Autoconf => ipv6_configuration_mode.Autoconf,
                _ => ipv6_configuration_mode.None
            };
            if (request.Mode == HostIpMode.Static)
            {
                if (pif.ipv6_configuration_mode == ipv6_configuration_mode.Static && pif.IPv6.Length > 1)
                    throw new InvalidOperationException("This interface has multiple static IPv6 addresses. Configure them with the server tools; this editor cannot preserve that address list.");
                var parts = request.Address.Trim().Split('/');
                if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix < 1 || prefix > 128)
                    throw new InvalidOperationException("Enter an IPv6 address with a prefix length from 1 to 128, such as 2001:db8::10/64.");
                var address = ParseAddress(parts[0], family, "IPv6 address");
                descriptor.IPv6 = [$"{address}/{prefix}"];
                descriptor.ipv6_gateway = string.IsNullOrWhiteSpace(request.Gateway) ? "" : ParseAddress(request.Gateway, family, "IPv6 gateway").ToString();
                if (connection.Cache.PIFs.Any(p => p.opaque_ref != pif.opaque_ref && p.IPv6.Any(ip => IPAddress.TryParse(ip.Split('/')[0], out var existing) && existing.Equals(address))))
                    throw new InvalidOperationException("That IPv6 address is already assigned to another interface in this pool.");
            }
            else
            {
                // Dynamic and disabled modes must not pass several cached lease addresses
                // to the API's single-address parameter.
                descriptor.IPv6 = [];
                descriptor.ipv6_gateway = "";
            }
            addressChanged = descriptor.ipv6_configuration_mode != pif.ipv6_configuration_mode || request.Mode == HostIpMode.Static
                && (!descriptor.IPv6.SequenceEqual(pif.IPv6) || descriptor.ipv6_gateway != pif.ipv6_gateway);
        }
        descriptor.DNS = string.Join(',', new[] { dns, DnsForFamily(pif.DNS, family == AddressFamily.InterNetwork ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork) }.Where(x => x.Length > 0));
        var reconnect = primary && addressChanged
            ? request.Mode == HostIpMode.Static
                ? $"The connection may close. Reconnect to {request.Address.Trim().Split('/')[0]} after confirming the new address on the host console. Verify the host certificate before trusting it."
                : "The connection may close. Find the new management address on the host console or DHCP server, then reconnect and verify the host certificate."
            : "Refresh the host interface after applying the change. Existing traffic on this interface may be interrupted.";
        return new(host, pif, descriptor, request.Family, addressChanged || previousDns != dns, primary && addressChanged, reconnect);
    }

    public static string DnsForFamily(string text, AddressFamily family) => string.Join(',', text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(value => IPAddress.TryParse(value, out var address) && address.AddressFamily == family));

    private static string ParseDns(string text, AddressFamily family) => string.Join(',', text.Split([',', '\n', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(value => ParseAddress(value, family, family == AddressFamily.InterNetwork ? "IPv4 DNS server" : "IPv6 DNS server").ToString()).Distinct());

    private static IPAddress ParseAddress(string text, AddressFamily family, string label)
    {
        text = text.Trim();
        if (family == AddressFamily.InterNetwork && (text.Split('.').Length != 4 || text.Split('.').Any(part => part.Length > 1 && part[0] == '0' || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            || text.Contains('%') || !IPAddress.TryParse(text, out var address) || address.AddressFamily != family
            || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6Multicast || address.IsIPv4MappedToIPv6
            || family == AddressFamily.InterNetwork && (address.GetAddressBytes()[0] == 0 || address.GetAddressBytes()[0] >= 224))
            throw new InvalidOperationException($"Enter a valid unicast {label}.");
        return address;
    }

    private static uint ParseMask(string text)
    {
        if (text.Trim().Split('.').Length != 4 || text.Trim().Split('.').Any(part => part.Length > 1 && part[0] == '0' || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            || !IPAddress.TryParse(text.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("Enter a valid IPv4 subnet mask.");
        var mask = ToUInt32(address);
        var inverse = ~mask;
        if (mask == 0 || (inverse & (inverse + 1)) != 0) throw new InvalidOperationException("The IPv4 subnet mask must contain contiguous network bits.");
        return mask;
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static bool IsNullReference(string? reference) => string.IsNullOrEmpty(reference) || reference == "OpaqueRef:NULL";

    // Serialize the mutable collections now, so later in-place cache mutations
    // cannot alter the baseline as they would with XenObject's shallow Clone().
    private static string Configuration(PIF pif) => JsonSerializer.Serialize(new
    {
        pif.device, pif.management, pif.primary_address_type, pif.currently_attached,
        pif.ip_configuration_mode, pif.IP, pif.netmask, pif.gateway, pif.DNS,
        pif.ipv6_configuration_mode, pif.IPv6, pif.ipv6_gateway,
        pif.VLAN, pif.MTU, pif.physical, pif.other_config,
        Bond = pif.bond_slave_of.opaque_ref,
        Bonds = pif.bond_master_of.Select(x => x.opaque_ref).Order().ToArray(),
        Vlans = pif.VLAN_slave_of.Select(x => x.opaque_ref).Order().ToArray()
    });
}

public sealed class HostIpConfigurationAction : AsyncAction
{
    private readonly HostIpEdit _request;

    public HostIpConfigurationAction(IXenConnection connection, HostIpEdit request)
        : base(connection, $"Configure host {request.Family} address")
    {
        _request = request;
        ApiMethodsToRoleCheck.Add(request.Family == HostIpFamily.IPv4 ? "pif.reconfigure_ip" : "pif.reconfigure_ipv6");
        if (request.Family == HostIpFamily.IPv4 && request.Mode != HostIpMode.None)
            ApiMethodsToRoleCheck.AddRange("pif.set_other_config", "pif.plug");
        AddCommonAPIMethodsToRoleCheck();
    }

    protected override void Run()
    {
        if (!Connection.IsConnected) throw new InvalidOperationException("The server disconnected. Reconnect and review the interface before trying again.");
        var plan = HostIpManagement.Plan(Connection, _request);
        if (!plan.Changed) { Description = "The IP configuration is unchanged."; return; }
        if (plan.Family == HostIpFamily.IPv4 && plan.Descriptor.ip_configuration_mode != ip_configuration_mode.None)
        {
            // Same single-host path as the supported WinForms IP editor. Passing
            // no new/down management PIF preserves the management interface.
            new ChangeNetworkingAction(Connection, null, plan.Host, [plan.Descriptor], [], null, null, plan.ManagementAddressChanged).RunSync(Session);
        }
        else
        {
            // Shared BringUp keeps the old IPv4 address for non-static modes.
            // None must send the reviewed empty fields directly. IPv6 also has
            // no shared editor action. Neither path changes topology or family.
            plan.Current.Locked = true;
            // Match ChangeNetworkingAction: retry transient disruption only if
            // the management address stays usable. A changed address requires
            // manual reconnect, not repeated attempts against the old endpoint.
            Connection.ExpectDisruption = !plan.ManagementAddressChanged;
            try
            {
                RelatedTask = plan.Family == HostIpFamily.IPv4
                    ? PIF.async_reconfigure_ip(Session, plan.Current.opaque_ref,
                        plan.Descriptor.ip_configuration_mode, plan.Descriptor.IP,
                        plan.Descriptor.netmask, plan.Descriptor.gateway, plan.Descriptor.DNS)
                    : PIF.async_reconfigure_ipv6(Session, plan.Current.opaque_ref,
                        plan.Descriptor.ipv6_configuration_mode, plan.Descriptor.IPv6.FirstOrDefault() ?? "",
                        plan.Descriptor.ipv6_gateway, plan.Descriptor.DNS);
                PollToCompletion();
            }
            finally
            {
                plan.Current.Locked = false;
                Connection.ExpectDisruption = false;
            }
        }
        Description = "Host IP configuration applied. " + plan.ReconnectNotice;
    }
}
