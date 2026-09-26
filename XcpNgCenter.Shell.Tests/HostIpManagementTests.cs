using System.Net.Sockets;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;
using Network = XenAPI.Network;

namespace XcpNgCenter.Shell.Tests;

public sealed class HostIpManagementTests
{
    [Fact]
    public void IPv4PlanChangesOneDetachedInterfaceAndPreservesManagementAndIPv6()
    {
        var f = new Fixture();
        var plan = HostIpManagement.Plan(f.Connection, f.IPv4 with { Address = "192.0.2.25", Dns = "192.0.2.53, 192.0.2.54" });
        Assert.True(plan.Changed);
        Assert.True(plan.ManagementAddressChanged);
        Assert.NotSame(f.Pif, plan.Descriptor);
        Assert.Equal("192.0.2.10", f.Pif.IP);
        Assert.Equal("192.0.2.25", plan.Descriptor.IP);
        Assert.True(plan.Descriptor.management);
        Assert.Equal(primary_address_type.IPv4, plan.Descriptor.primary_address_type);
        Assert.Equal(f.Pif.IPv6, plan.Descriptor.IPv6);
        Assert.Contains("2001:db8::53", plan.Descriptor.DNS);
        Assert.Contains("192.0.2.25", plan.ReconnectNotice);
        Assert.False(f.Pif.Locked);
    }

    [Fact]
    public void UnchangedSettingsDoNotScheduleReconfigurationAndDnsOnlyIsNotAddressChange()
    {
        var f = new Fixture();
        Assert.False(HostIpManagement.Plan(f.Connection, f.IPv4).Changed);
        Assert.False(HostIpManagement.Plan(f.Connection, f.IPv6).Changed);
        var plan = HostIpManagement.Plan(f.Connection, f.IPv4 with { Dns = "192.0.2.54" });
        Assert.True(plan.Changed);
        Assert.False(plan.ManagementAddressChanged);
        Assert.Contains("2001:db8::53", plan.Descriptor.DNS);
    }

    [Fact]
    public void IPv6StaticRetainsIPv4DnsAndRequiresAnExplicitPrefix()
    {
        var f = new Fixture();
        var plan = HostIpManagement.Plan(f.Connection, f.IPv6 with { Address = "2001:db8::25/64", Dns = "2001:db8::54" });
        Assert.Equal(new[] { "2001:db8::25/64" }, plan.Descriptor.IPv6);
        Assert.Equal("192.0.2.10", plan.Descriptor.IP);
        Assert.Equal("2001:db8::54,192.0.2.53", plan.Descriptor.DNS);
        Assert.False(plan.ManagementAddressChanged);
        Assert.Equal(new[] { "2001:db8::10/64" }, f.Pif.IPv6);
    }

    [Theory]
    [InlineData("192.0.2")]
    [InlineData("0xc000020a")]
    [InlineData("192.0.2.010")]
    [InlineData("127.0.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.2.0")]
    [InlineData("192.0.2.255")]
    [InlineData("2001:db8::1")]
    public void InvalidIPv4InterfaceAddressesAreRejected(string address)
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv4 with { Address = address }));
    }

    [Theory]
    [InlineData("255.0.255.0")]
    [InlineData("0.0.0.0")]
    [InlineData("24")]
    [InlineData("ffff:ffff::")]
    public void InvalidIPv4MasksAreRejected(string mask)
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv4 with { Netmask = mask }));
    }

    [Theory]
    [InlineData("2001:db8::1")]
    [InlineData("2001:db8::1/129")]
    [InlineData("2001:db8::1/0")]
    [InlineData("::1/64")]
    [InlineData("::/64")]
    [InlineData("ff02::1/64")]
    [InlineData("fe80::1%eth0/64")]
    [InlineData("192.0.2.10/24")]
    public void InvalidIPv6InterfaceAddressesAreRejected(string address)
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv6 with { Address = address }));
    }

    [Fact]
    public void GatewayAndDnsRequireUsableAddressesOfTheSelectedFamily()
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv4 with { Gateway = "198.51.100.1" }));
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv4 with { Dns = "2001:db8::53" }));
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv6 with { Dns = "192.0.2.53" }));
        Assert.Equal("", HostIpManagement.Plan(f.Connection, f.IPv4 with { Gateway = "", Dns = "" }).Descriptor.gateway);
    }

    [Theory]
    [InlineData("192.0.2.0")]
    [InlineData("192.0.2.255")]
    [InlineData("192.0.2.10")]
    public void GatewayCannotBeNetworkBroadcastOrSelf(string gateway)
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv4 with { Gateway = gateway }));
    }

    [Fact]
    public void DnsOnlyEditCannotSilentlyReplaceMultipleStaticIPv6Addresses()
    {
        var f = new Fixture();
        f.Pif.IPv6 = ["2001:db8::10/64", "2001:db8::11/64"];
        var error = Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv6 with { Dns = "2001:db8::54" }));
        Assert.Contains("multiple static IPv6", error.Message);
        Assert.Equal(2, f.Pif.IPv6.Length);
    }

    [Fact]
    public void DuplicateAddressesAreRejectedAcrossPoolInterfaces()
    {
        var f = new Fixture();
        f.Add("other-pif", new PIF { IP = "192.0.2.25", IPv6 = ["2001:db8::25/48"] });
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv4 with { Address = "192.0.2.25" }));
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv6 with { Address = "2001:db8::25/64" }));
    }

    [Theory]
    [InlineData("ha")]
    [InlineData("cluster")]
    [InlineData("protected")]
    [InlineData("unmanaged")]
    [InlineData("detached")]
    [InlineData("bond-member")]
    [InlineData("tunnel")]
    [InlineData("sriov")]
    [InlineData("host-offline")]
    [InlineData("host-busy")]
    [InlineData("pif-busy")]
    [InlineData("pif-identity-missing")]
    [InlineData("host-identity-missing")]
    public void NewlyProtectedOrUnavailableInterfacesAreRecheckedBeforeSubmission(string reason)
    {
        var f = new Fixture();
        var request = f.IPv4;
        switch (reason)
        {
            case "ha": f.Pool.ha_enabled = true; break;
            case "cluster": f.Add("cluster", new Cluster_host { PIF = new(f.Pif.opaque_ref) }); break;
            case "protected": f.Pif.disallow_unplug = true; break;
            case "unmanaged": f.Pif.managed = false; break;
            case "detached": f.Pif.currently_attached = false; break;
            case "bond-member": f.Pif.bond_slave_of = new("unresolved-bond"); break;
            case "tunnel": f.Pif.tunnel_transport_PIF_of = [new("unresolved-tunnel")]; break;
            case "sriov": f.Pif.sriov_logical_PIF_of = [new("unresolved-sriov")]; break;
            case "host-offline": f.Metrics.live = false; break;
            case "host-busy": f.Host.Locked = true; break;
            case "pif-busy": f.Pif.Locked = true; break;
            case "pif-identity-missing": f.Pif.uuid = ""; break;
            case "host-identity-missing": f.Host.uuid = ""; break;
        }
        Assert.NotNull(HostIpManagement.InterfaceError(f.Pif));
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, request));
    }

    [Theory]
    [InlineData("host-identity")]
    [InlineData("pif-identity")]
    [InlineData("network-identity")]
    [InlineData("moved-host")]
    [InlineData("address")]
    [InlineData("management")]
    [InlineData("ipv6-array")]
    [InlineData("metadata")]
    public void StaleConfigurationAndReplacedIdentitiesCannotBeApplied(string change)
    {
        var f = new Fixture();
        var request = f.IPv4;
        switch (change)
        {
            case "host-identity": f.Host.uuid = "replacement-host"; break;
            case "pif-identity": f.Pif.uuid = "replacement-pif"; break;
            case "network-identity": f.Network.uuid = "replacement-network"; break;
            case "moved-host": f.Pif.host = new("another-host"); break;
            case "address": f.Pif.IP = "192.0.2.99"; break;
            case "management": f.Pif.management = false; break;
            case "ipv6-array": f.Pif.IPv6[0] = "2001:db8::99/64"; break;
            case "metadata": f.Pif.other_config["management_purpose"] = "Storage"; break;
        }
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, request));
    }

    [Fact]
    public void PrimaryManagementFamilyCannotBeDisabledButSecondaryFamilyCan()
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv4 with { Mode = HostIpMode.None }));
        Assert.Equal(ipv6_configuration_mode.None, HostIpManagement.Plan(f.Connection, f.IPv6 with { Mode = HostIpMode.None }).Descriptor.ipv6_configuration_mode);
        f.Pif.primary_address_type = primary_address_type.IPv6;
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv6 with { Mode = HostIpMode.None }));
        Assert.Equal(ip_configuration_mode.None, HostIpManagement.Plan(f.Connection, f.IPv4 with { Mode = HostIpMode.None }).Descriptor.ip_configuration_mode);
    }

    [Fact]
    public void DynamicModesDoNotSubmitCachedIPv6LeaseListsOrInventReconnectAddresses()
    {
        var f = new Fixture();
        f.Pif.primary_address_type = primary_address_type.IPv6;
        var plan = HostIpManagement.Plan(f.Connection, f.IPv6 with { Mode = HostIpMode.Autoconf });
        Assert.Empty(plan.Descriptor.IPv6);
        Assert.Equal("", plan.Descriptor.ipv6_gateway);
        Assert.True(plan.ManagementAddressChanged);
        Assert.Contains("host console or DHCP", plan.ReconnectNotice);
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv4 with { Mode = HostIpMode.Autoconf }));
        f.Pif.ipv6_configuration_mode = ipv6_configuration_mode.Autoconf;
        Assert.False(HostIpManagement.Plan(f.Connection, f.IPv6 with { Mode = HostIpMode.Autoconf }).Changed);
    }

    [Fact]
    public void OldOrUnknownServersDoNotExposeIPv6Configuration()
    {
        var f = new Fixture();
        f.Host.software_version = new() { ["product_version"] = "6.0.0" };
        Assert.False(HostIpManagement.SupportsIpv6(f.Host));
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, f.IPv6));
        f.Host.software_version = new();
        Assert.False(HostIpManagement.SupportsIpv6(f.Host));
    }

    [Fact]
    public void EditorRetainsDraftAcrossInventoryChangesAndUsesCapturedBaseline()
    {
        var f = new Fixture();
        var editor = new HostIpEditorViewModel(f.Connection, f.Host, () => { });
        editor.Address = "192.0.2.25";
        f.Pif.IP = "192.0.2.99";
        Assert.Equal("192.0.2.25", editor.Address);
        Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, editor.Request));
    }

    [Fact]
    public void EditorShowsIncompleteIdentityWithoutThrowingOrAllowingSave()
    {
        var f = new Fixture();
        f.Network.uuid = "";
        var editor = new HostIpEditorViewModel(f.Connection, f.Host, () => { });
        Assert.False(editor.CanSave);
        Assert.Contains("incomplete", editor.InterfaceNotice);
        Assert.Throws<InvalidOperationException>(() => editor.Request);
    }

    [Fact]
    public void DnsFamilySelectionPreservesOtherFamilyAndIgnoresEmptySeparators()
    {
        Assert.Equal("192.0.2.53", HostIpManagement.DnsForFamily("192.0.2.53, ,2001:db8::53", AddressFamily.InterNetwork));
        Assert.Equal("2001:db8::53", HostIpManagement.DnsForFamily("192.0.2.53, ,2001:db8::53", AddressFamily.InterNetworkV6));
    }

    [Theory]
    [InlineData("missing-selected")]
    [InlineData("duplicate-selected")]
    [InlineData("unresolved-link")]
    [InlineData("extra-same-host")]
    [InlineData("extra-inverse-only")]
    [InlineData("foreign-network-link")]
    public void IPv4SharedActionCannotSkipOrExpandTheReviewedInterface(string inconsistency)
    {
        var f = new Fixture();
        var request = f.IPv4 with { Address = "192.0.2.25" };
        switch (inconsistency)
        {
            case "missing-selected": f.Network.PIFs = []; break;
            case "duplicate-selected": f.Network.PIFs = [new("pif"), new("pif")]; break;
            case "unresolved-link": f.Network.PIFs = [new("pif"), new("unresolved")]; break;
            default:
                f.Add("extra-pif", new PIF { host = new("host"), network = new(inconsistency == "foreign-network-link" ? "other-network" : "network") });
                if (inconsistency != "extra-inverse-only") f.Network.PIFs = [new("pif"), new("extra-pif")];
                break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => HostIpManagement.Plan(f.Connection, request));
        Assert.Contains("incomplete or ambiguous", error.Message);
        Assert.Equal("192.0.2.10", f.Pif.IP);
    }

    [Fact]
    public void IPv4ActionDeclaresNestedMetadataAndPlugPermissionsBeforeMutation()
    {
        var f = new Fixture();
        var methods = new HostIpConfigurationAction(f.Connection, f.IPv4).GetApiMethodsToRoleCheck.Select(m => m.Method).ToArray();
        Assert.Contains("pif.reconfigure_ip", methods);
        Assert.Contains("pif.set_other_config", methods);
        Assert.Contains("pif.plug", methods);
        Assert.DoesNotContain("pif.reconfigure_ipv6", methods);
        Assert.False(f.Pif.Locked);
    }

    private sealed class Fixture
    {
        public XenConnection Connection { get; } = new();
        public Host Host { get; }
        public Host_metrics Metrics { get; }
        public Pool Pool { get; }
        public Network Network { get; }
        public PIF Pif { get; }

        public Fixture()
        {
            Metrics = Add("metrics", new Host_metrics { live = true });
            Host = Add("host", new Host { uuid = "host-uuid", name_label = "Test host", metrics = new("metrics"), software_version = new() { ["product_version"] = "8.3.0" } });
            Pool = Add("pool", new Pool { master = new("host") });
            Network = Add("network", new Network { uuid = "network-uuid", name_label = "Management", PIFs = [new("pif")] });
            Pif = Add("pif", new PIF
            {
                uuid = "pif-uuid", host = new("host"), network = new("network"), device = "eth0", VLAN = -1,
                physical = true, managed = true, currently_attached = true, management = true,
                ip_configuration_mode = ip_configuration_mode.Static, IP = "192.0.2.10", netmask = "255.255.255.0", gateway = "192.0.2.1",
                ipv6_configuration_mode = ipv6_configuration_mode.Static, IPv6 = ["2001:db8::10/64"], ipv6_gateway = "2001:db8::1",
                DNS = "192.0.2.53,2001:db8::53"
            });
        }

        public HostIpEdit IPv4 => new(HostIpManagement.Capture(Host, Pif), HostIpFamily.IPv4, HostIpMode.Static, Pif.IP, Pif.netmask, Pif.gateway, "192.0.2.53");
        public HostIpEdit IPv6 => new(HostIpManagement.Capture(Host, Pif), HostIpFamily.IPv6, HostIpMode.Static, Pif.IPv6[0], "", Pif.ipv6_gateway, "2001:db8::53");

        public T Add<T>(string reference, T value) where T : XenObject<T>
        {
            Connection.Cache.UpdateFrom(Connection, [new ObjectChange(typeof(T), reference, value)]);
            return Connection.Resolve(new XenRef<T>(reference));
        }
    }
}
