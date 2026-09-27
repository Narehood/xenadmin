using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;
using Network = XenAPI.Network;

namespace XcpNgCenter.Shell.Tests;

public sealed class SriovNetworkManagementTests
{
    [Fact]
    public void PlanContainsEveryHostCoordinatorFirstAndDoesNotChangeExistingNetworks()
    {
        var fixture = new Fixture();
        var plan = SriovNetworkManagement.Plan(fixture.Connection, fixture.Request with { Name = "  SR-IOV  ", Automatic = true });

        Assert.Equal("SR-IOV", plan.Descriptor.name_label);
        Assert.True(plan.Descriptor.GetAutoPlug());
        Assert.Equal(new[] { "nic-0", "nic-1" }, plan.Pifs.Select(p => p.opaque_ref));
        Assert.Equal(1500, plan.Descriptor.MTU);
        Assert.Single(fixture.Connection.Cache.Networks);
        Assert.All(plan.Pifs, p => Assert.False(p.Locked));
        Assert.Empty(fixture.Connection.Cache.Networks[0].VIFs);
    }

    [Theory]
    [InlineData("management")]
    [InlineData("ipv4")]
    [InlineData("ipv6")]
    [InlineData("protected")]
    [InlineData("cluster")]
    [InlineData("bond-member")]
    [InlineData("bond-member-unresolved")]
    [InlineData("bond-master")]
    [InlineData("vlan")]
    [InlineData("tunnel")]
    [InlineData("sriov")]
    [InlineData("sriov-reverse")]
    [InlineData("vm")]
    [InlineData("unresolved-vif")]
    [InlineData("unsupported")]
    [InlineData("unmanaged")]
    [InlineData("virtual")]
    [InlineData("offline")]
    [InlineData("busy-host")]
    [InlineData("restricted")]
    [InlineData("old-server")]
    [InlineData("unknown-model")]
    [InlineData("different-model")]
    [InlineData("missing-pif-identity")]
    [InlineData("missing-host-identity")]
    [InlineData("missing-network-identity")]
    public void UnsafeOrUnsupportedMemberRejectsWholePoolBeforeAnyMutation(string hazard)
    {
        var fixture = new Fixture();
        var request = fixture.Request;
        var pif = fixture.Connection.Resolve(new XenRef<PIF>("nic-1"));
        var host = fixture.Connection.Resolve(pif.host);
        switch (hazard)
        {
            case "management": pif.management = true; break;
            case "ipv4": pif.ip_configuration_mode = ip_configuration_mode.DHCP; break;
            case "ipv6": pif.ipv6_configuration_mode = ipv6_configuration_mode.Autoconf; break;
            case "protected": pif.disallow_unplug = true; break;
            case "cluster": fixture.Add("cluster", new Cluster_host { PIF = new("nic-1") }); break;
            case "bond-member": fixture.Add("bond", new Bond { slaves = [new("nic-1")] }); break;
            case "bond-member-unresolved": pif.bond_slave_of = new("bond-not-yet-in-cache"); break;
            case "bond-master": fixture.Add("bond", new Bond { master = new("nic-1") }); break;
            case "vlan": fixture.Add("vlan", new VLAN { tagged_PIF = new("nic-1") }); break;
            case "tunnel": fixture.Add("tunnel", new Tunnel { transport_PIF = new("nic-1") }); break;
            case "sriov": pif.sriov_physical_PIF_of = [new("existing")]; break;
            case "sriov-reverse": fixture.Add("existing", new Network_sriov { physical_PIF = new("nic-1") }); break;
            case "vm": fixture.Add("vif", new VIF { network = pif.network, currently_attached = false }); break;
            case "unresolved-vif": fixture.Connection.Resolve(pif.network).VIFs = [new("not-in-cache")]; break;
            case "unsupported": pif.capabilities = []; break;
            case "unmanaged": pif.managed = false; break;
            case "virtual": pif.physical = false; break;
            case "offline": fixture.Connection.Resolve(host.metrics).live = false; break;
            case "busy-host": host.current_operations = new() { ["task"] = host_allowed_operations.evacuate }; break;
            case "restricted": host.license_params = new() { ["restrict_network_sriov"] = "true" }; break;
            case "old-server": host.software_version = new() { ["platform_version"] = "2.0.0" }; break;
            case "unknown-model": fixture.Connection.Resolve(pif.metrics).vendor_id = ""; break;
            case "different-model": fixture.Connection.Resolve(pif.metrics).device_id = "1234"; break;
            case "missing-pif-identity": pif.uuid = ""; break;
            case "missing-host-identity": host.uuid = ""; break;
            case "missing-network-identity": fixture.Connection.Resolve(pif.network).uuid = ""; break;
        }

        Assert.Throws<InvalidOperationException>(() => SriovNetworkManagement.Plan(fixture.Connection, request));
        Assert.All(SriovNetworkManagement.Uplinks(fixture.Connection), option => Assert.NotNull(option.Error));
        Assert.Single(fixture.Connection.Cache.Networks);
    }

    [Theory]
    [InlineData("uuid")]
    [InlineData("mac")]
    [InlineData("device")]
    [InlineData("network")]
    [InlineData("host")]
    [InlineData("host-uuid")]
    [InlineData("network-uuid")]
    public void RevalidationRejectsReplacedOrReassignedNic(string field)
    {
        var fixture = new Fixture();
        var request = fixture.Request;
        _ = SriovNetworkManagement.Plan(fixture.Connection, request);
        var pif = fixture.Connection.Resolve(new XenRef<PIF>("nic-1"));
        switch (field)
        {
            case "uuid": pif.uuid = "replacement"; break;
            case "mac": pif.MAC = "02:00:00:00:00:99"; break;
            case "device": pif.device = "eth2"; break;
            case "network": pif.network = new("replacement"); break;
            case "host": pif.host = new("host-0"); break;
            case "host-uuid": fixture.Connection.Resolve(pif.host).uuid = "replacement-host"; break;
            case "network-uuid": fixture.Connection.Resolve(pif.network).uuid = "replacement-network"; break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => SriovNetworkManagement.Plan(fixture.Connection, request));
        Assert.Contains("changed", error.Message);
    }

    [Fact]
    public void AddingPoolMemberAfterReviewDoesNotExpandTheOperation()
    {
        var fixture = new Fixture();
        var request = fixture.Request;
        fixture.AddHost(2);
        Assert.Contains("pool host", Assert.Throws<InvalidOperationException>(() =>
            SriovNetworkManagement.Plan(fixture.Connection, request)).Message);
        Assert.Equal(3, SriovNetworkManagement.Plan(fixture.Connection, fixture.Request).Pifs.Count);
    }

    [Fact]
    public void IncompletePoolOrDuplicateTargetsAreRejected()
    {
        var fixture = new Fixture();
        var request = fixture.Request;
        Assert.Throws<InvalidOperationException>(() => SriovNetworkManagement.Plan(fixture.Connection, request with { Targets = [request.Targets[0]] }));
        Assert.Throws<InvalidOperationException>(() => SriovNetworkManagement.Plan(fixture.Connection, request with { Targets = [request.Targets[0], request.Targets[0]] }));
        Assert.Throws<InvalidOperationException>(() => SriovNetworkManagement.Plan(fixture.Connection, request with { Name = " " }));
        fixture.Add("host-2", new Host());
        Assert.NotNull(Assert.Single(SriovNetworkManagement.Uplinks(fixture.Connection)).Error);
    }

    [Fact]
    public void RefreshKeepsDraftAndExplainsNewlyUnsafeSelection()
    {
        var fixture = new Fixture();
        var editor = new SriovNetworkViewModel(fixture.Connection, () => { }, _ => { })
        {
            NameLabel = "My network", Description = "Retain this draft", Automatic = true
        };
        Assert.True(editor.CanCreate);
        fixture.Connection.Resolve(new XenRef<PIF>("nic-1")).management = true;
        editor.RefreshCommand.Execute(null);
        Assert.False(editor.CanCreate);
        Assert.Contains("Management", editor.SelectionNotice);
        Assert.Equal("My network", editor.NameLabel);
        Assert.Equal("Retain this draft", editor.Description);
        Assert.True(editor.Automatic);
    }

    [Fact]
    public void UnusedSriovNetworkCanBeRemovedWithoutIncludingPhysicalNics()
    {
        var fixture = new Fixture();
        var network = fixture.AddSriovNetwork();
        var request = SriovNetworkManagement.CaptureRemoval(network);
        Assert.Equal(2, request.Targets.Count);
        Assert.All(request.Targets, target => Assert.StartsWith("logical-", target.LogicalReference));
        Assert.Same(network, SriovNetworkManagement.PlanRemoval(fixture.Connection, request));
        Assert.Null(SriovNetworkManagement.RemovalError(network));
        Assert.All(fixture.Connection.Cache.PIFs, pif => Assert.False(pif.Locked));
    }

    [Theory]
    [InlineData("vm")]
    [InlineData("unresolved-vif")]
    [InlineData("vlan")]
    [InlineData("tunnel")]
    [InlineData("management")]
    [InlineData("ipv4")]
    [InlineData("cluster")]
    [InlineData("physical-protected")]
    [InlineData("physical-vm")]
    [InlineData("reboot")]
    [InlineData("replaced-logical")]
    [InlineData("physical-in-network")]
    [InlineData("unresolved-pif")]
    [InlineData("backlink-changed")]
    [InlineData("offline")]
    [InlineData("network-replaced")]
    [InlineData("bond-member-unresolved")]
    [InlineData("bond-member-reverse")]
    [InlineData("sriov-replaced")]
    [InlineData("host-replaced")]
    [InlineData("physical-network-replaced")]
    public void RemovalRechecksDependenciesAndExactTopologyAfterConfirmation(string hazard)
    {
        var fixture = new Fixture();
        var network = fixture.AddSriovNetwork();
        var request = SriovNetworkManagement.CaptureRemoval(network);
        var logical = fixture.Connection.Resolve(new XenRef<PIF>("logical-1"));
        switch (hazard)
        {
            case "vm": fixture.Add("vif", new VIF { network = new("sriov-network"), currently_attached = false }); break;
            case "unresolved-vif": network.VIFs = [new("unresolved")]; break;
            case "vlan": fixture.Add("vlan", new VLAN { tagged_PIF = new("logical-1") }); break;
            case "tunnel": fixture.Add("tunnel", new Tunnel { transport_PIF = new("logical-1") }); break;
            case "management": logical.management = true; break;
            case "ipv4": logical.ip_configuration_mode = ip_configuration_mode.Static; break;
            case "cluster": fixture.Add("cluster", new Cluster_host { PIF = new("logical-1") }); break;
            case "physical-protected": fixture.Connection.Resolve(new XenRef<PIF>("nic-1")).management = true; break;
            case "physical-vm": fixture.Add("vif", new VIF { network = new("physical") }); break;
            case "reboot": fixture.Connection.Resolve(new XenRef<Network_sriov>("sriov-1")).requires_reboot = true; break;
            case "replaced-logical": logical.uuid = "replacement"; break;
            case "physical-in-network": logical.physical = true; break;
            case "unresolved-pif": network.PIFs = [.. network.PIFs, new("unresolved")]; break;
            case "backlink-changed": fixture.Connection.Resolve(new XenRef<Network_sriov>("sriov-1")).physical_PIF = new("nic-0"); break;
            case "offline": fixture.Connection.Resolve(new XenRef<Host_metrics>("health-1")).live = false; break;
            case "network-replaced": network.uuid = "replacement"; break;
            case "bond-member-unresolved": logical.bond_slave_of = new("unresolved-bond"); break;
            case "bond-member-reverse": fixture.Add("bond", new Bond { slaves = [new("logical-1")] }); break;
            case "sriov-replaced": fixture.Connection.Resolve(new XenRef<Network_sriov>("sriov-1")).uuid = "replacement"; break;
            case "host-replaced": fixture.Connection.Resolve(logical.host).uuid = "replacement"; break;
            case "physical-network-replaced": fixture.Connection.Resolve(new XenRef<Network>("physical")).uuid = "replacement"; break;
        }
        Assert.Throws<InvalidOperationException>(() => SriovNetworkManagement.PlanRemoval(fixture.Connection, request));
        Assert.Equal(2, fixture.Connection.Cache.Networks.Length);
    }

    private sealed class Fixture
    {
        public XenConnection Connection { get; } = new();
        public SriovNetworkRequest Request => new("SR-IOV", "", false,
            Assert.Single(SriovNetworkManagement.Uplinks(Connection)).Targets);

        public Fixture()
        {
            Add("pool", new Pool { master = new("host-0") });
            Add("physical", new Network { uuid = "physical-network-uuid", name_label = "Physical", PIFs = [new("nic-0"), new("nic-1")] });
            AddHost(0);
            AddHost(1);
        }

        public void AddHost(int index)
        {
            Add($"health-{index}", new Host_metrics { live = true });
            Add($"host-{index}", new Host
            {
                name_label = $"Host {index}", uuid = $"host-uuid-{index}", metrics = new($"health-{index}"),
                software_version = new() { ["platform_version"] = "3.0.0" },
                license_params = new() { ["restrict_network_sriov"] = "false" }
            });
            Add($"metrics-{index}", new PIF_metrics { vendor_id = "8086", device_id = "1572" });
            Add($"nic-{index}", new PIF
            {
                uuid = $"nic-uuid-{index}", host = new($"host-{index}"), network = new("physical"), device = "eth1",
                physical = true, managed = true, VLAN = -1, capabilities = ["sriov"],
                MAC = $"02:00:00:00:00:{index:D2}", metrics = new($"metrics-{index}"),
                ip_configuration_mode = ip_configuration_mode.None, ipv6_configuration_mode = ipv6_configuration_mode.None
            });
        }

        public Network AddSriovNetwork()
        {
            Add("sriov-network", new Network { uuid = "sriov-network-uuid", name_label = "SR-IOV", PIFs = [new("logical-0"), new("logical-1")] });
            for (var index = 0; index < 2; index++)
            {
                Connection.Resolve(new XenRef<PIF>($"nic-{index}")).sriov_physical_PIF_of = [new($"sriov-{index}")];
                Add($"logical-{index}", new PIF
                {
                    uuid = $"logical-uuid-{index}", physical = false, managed = true, VLAN = -1,
                    host = new($"host-{index}"), network = new("sriov-network"),
                    sriov_logical_PIF_of = [new($"sriov-{index}")],
                    ip_configuration_mode = ip_configuration_mode.None, ipv6_configuration_mode = ipv6_configuration_mode.None
                });
                Add($"sriov-{index}", new Network_sriov
                {
                    uuid = $"sriov-uuid-{index}",
                    physical_PIF = new($"nic-{index}"), logical_PIF = new($"logical-{index}"), requires_reboot = false
                });
            }
            return Connection.Resolve(new XenRef<Network>("sriov-network"));
        }

        public void Add<T>(string reference, T value) where T : XenObject<T> =>
            Connection.Cache.UpdateFrom(Connection, [new ObjectChange(typeof(T), reference, value)]);
    }
}
