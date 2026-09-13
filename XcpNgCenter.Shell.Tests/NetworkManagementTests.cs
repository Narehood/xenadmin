using XenAdmin.Network;
using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;
using Network = XenAPI.Network;

namespace XcpNgCenter.Shell.Tests;

public sealed class NetworkManagementTests
{
    [Fact]
    public void PrivateAndVlanCreationUseDetachedDescriptorsAndCoordinatorUplink()
    {
        var f = new Fixture();
        var privatePlan = NetworkManagement.PlanNetwork(f.Connection, f.NewNetwork with { External = false, Tags = "prod\n prod \nbackup" });
        Assert.Null(privatePlan.Current);
        Assert.Null(privatePlan.Uplink);
        Assert.Equal(new[] { "prod", "backup" }, privatePlan.Descriptor.tags);
        Assert.DoesNotContain(f.Connection.Cache.Networks, n => n.name_label == "New VLAN");
        var vlan = NetworkManagement.PlanNetwork(f.Connection, f.NewNetwork);
        Assert.Same(f.Uplink, vlan.Uplink);
        Assert.Equal(42, vlan.Vlan);
        Assert.True(vlan.External);
        Assert.False(f.Uplink.Locked);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("4095")]
    [InlineData("1.5")]
    [InlineData("abc")]
    public void InvalidVlanIsRejected(string vlan)
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanNetwork(f.Connection, f.NewNetwork with { Vlan = vlan }));
    }

    [Fact]
    public void VlanZeroRequiresVswitchAndDuplicateOnAnotherHostIsRejected()
    {
        var f = new Fixture();
        Assert.Equal(0, NetworkManagement.PlanNetwork(f.Connection, f.NewNetwork with { Vlan = "0" }).Vlan);
        f.Host.software_version = new Dictionary<string, string> { ["network_backend"] = "bridge" };
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanNetwork(f.Connection, f.NewNetwork with { Vlan = "0" }));
        f.Add("other-host", new Host());
        f.Add("duplicate", new PIF { VLAN = 42, device = "eth0", host = new("other-host"), network = new("other-network") });
        Assert.Contains("already exists", Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanNetwork(f.Connection, f.NewNetwork)).Message);
    }

    [Fact]
    public void VlanEditRetainsIdentityAndUnchangedVlanDoesNotRecreateInterfaces()
    {
        var f = new Fixture();
        var request = f.EditNetwork with { Name = "Renamed", Tags = "production" };
        var unchanged = NetworkManagement.PlanNetwork(f.Connection, request);
        Assert.False(unchanged.TopologyChanged);
        Assert.Equal(f.Vlan.opaque_ref, unchanged.Descriptor.opaque_ref);
        Assert.Equal("VLAN 10", f.Vlan.name_label);
        Assert.Empty(f.Vlan.tags);
        Assert.False(f.Vlan.Locked);
        Assert.True(NetworkManagement.PlanNetwork(f.Connection, request with { Vlan = "20" }).TopologyChanged);
        Assert.True(NetworkManagement.PlanNetwork(f.Connection, request with { External = false }).TopologyChanged);
    }

    [Theory]
    [InlineData("management")]
    [InlineData("ipv4")]
    [InlineData("ipv6")]
    [InlineData("disallow")]
    [InlineData("cluster")]
    [InlineData("physical")]
    [InlineData("bond")]
    [InlineData("tunnel")]
    [InlineData("sriov")]
    public void ProtectedTopologyCannotBeChangedOrRemoved(string protection)
    {
        var f = new Fixture();
        switch (protection)
        {
            case "management": f.VlanPif.management = true; break;
            case "ipv4": f.VlanPif.ip_configuration_mode = ip_configuration_mode.Static; break;
            case "ipv6": f.VlanPif.ipv6_configuration_mode = ipv6_configuration_mode.Autoconf; break;
            case "disallow": f.VlanPif.disallow_unplug = true; break;
            case "cluster": f.Add("cluster-host", new Cluster_host { PIF = new(f.VlanPif.opaque_ref) }); break;
            case "physical": f.VlanPif.VLAN = -1; f.VlanPif.physical = true; break;
            case "bond": f.VlanPif.bond_master_of = [new("bond")]; break;
            case "tunnel": f.VlanPif.tunnel_access_PIF_of = [new("tunnel")]; break;
            case "sriov": f.VlanPif.sriov_logical_PIF_of = [new("sriov")]; break;
        }
        Assert.NotNull(NetworkManagement.TopologyError(f.Vlan));
        Assert.NotNull(NetworkManagement.RemoveNetworkError(f.Vlan));
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanNetwork(f.Connection, f.EditNetwork with { Vlan = "20" }));
        // Metadata remains editable, with topology explicitly retained by the editor.
        var editor = new NetworkEditorViewModel(f.Connection, f.Vlan, () => { });
        Assert.False(editor.CanEditTopology);
        editor.NameLabel = "Renamed";
        Assert.False(NetworkManagement.PlanNetwork(f.Connection, editor.Request).TopologyChanged);
    }

    [Fact]
    public void StoppedVmStillBlocksNetworkRemovalAndAttachedVmBlocksVlanAndMtuChanges()
    {
        var f = new Fixture();
        var vif = f.AddVif("0");
        Assert.NotNull(NetworkManagement.RemoveNetworkError(f.Vlan));
        Assert.Null(NetworkManagement.TopologyError(f.Vlan));
        vif.currently_attached = true;
        Assert.NotNull(NetworkManagement.TopologyError(f.Vlan));
        Assert.NotNull(NetworkManagement.MtuError(f.Vlan));
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanNetwork(f.Connection, f.EditNetwork with { Vlan = "22" }));
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanNetwork(f.Connection, f.EditNetwork with { Mtu = "9000" }));
    }

    [Fact]
    public void NewProtectionAfterEditorOpenedIsRecheckedOnSave()
    {
        var f = new Fixture();
        var editor = new NetworkEditorViewModel(f.Connection, f.Vlan, () => { }) { Vlan = "22" };
        Assert.True(editor.CanEditTopology);
        f.VlanPif.management = true;
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanNetwork(f.Connection, editor.Request));
        f.VlanPif.management = false;
        f.Vlan.Locked = true;
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanNetwork(f.Connection, editor.Request));
    }

    [Fact]
    public void MetadataCloneDoesNotMutateCacheAndKeepsUnknownConfiguration()
    {
        var f = new Fixture();
        f.Vlan.other_config = new Dictionary<string, string> { ["custom"] = "retained" };
        var plan = NetworkManagement.PlanNetwork(f.Connection, f.EditNetwork with { Automatic = false, Description = "Edited", Mtu = "9000" });
        Assert.True(plan.MtuChanged);
        Assert.Equal("retained", plan.Descriptor.other_config["custom"]);
        Assert.False(plan.Descriptor.GetAutoPlug());
        Assert.True(f.Vlan.GetAutoPlug());
        Assert.Equal(1500, f.Vlan.MTU);
        Assert.NotSame(f.Vlan.other_config, plan.Descriptor.other_config);
    }

    [Theory]
    [InlineData("1499")]
    [InlineData("9217")]
    [InlineData("text")]
    public void InvalidMtuIsRejected(string mtu)
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanNetwork(f.Connection, f.NewNetwork with { Mtu = mtu }));
    }

    [Fact]
    public void NewVifReusesFreeDeviceAndRechecksMaximumCount()
    {
        var f = new Fixture();
        f.AddVif("0");
        f.AddVif("2");
        var descriptor = NetworkManagement.PlanVif(f.Vm, f.NewVif);
        Assert.Equal("1", descriptor.device);
        Assert.Equal(f.Vm.opaque_ref, descriptor.VM.opaque_ref);
        Assert.Equal(f.Vlan.opaque_ref, descriptor.network.opaque_ref);
        Assert.Equal("", descriptor.MAC);
        f.Vm.reference_label = "test-template";
        f.Add("template", new VM { is_a_template = true, reference_label = "test-template",
            recommendations = "<restrictions><restriction field=\"number-of-vifs\" max=\"2\" /></restrictions>" });
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanVif(f.Vm, f.NewVif));
    }

    [Fact]
    public void VifEditPreservesAdvancedSettingsAndDoesNotChangeOriginal()
    {
        var f = new Fixture();
        var vif = f.AddVif("3");
        vif.MAC = "02:00:00:00:00:01";
        vif.locking_mode = vif_locking_mode.locked;
        vif.ipv4_allowed = ["192.0.2.1"];
        vif.other_config = new Dictionary<string, string> { ["custom"] = "retained" };
        vif.qos_algorithm_params = new Dictionary<string, string> { ["extra"] = "retained" };
        var descriptor = NetworkManagement.PlanVif(f.Vm, f.NewVif with { Reference = vif.opaque_ref, Mac = vif.MAC, Limit = true, Rate = "1024" });
        Assert.Null(descriptor.opaque_ref);
        Assert.Equal("3", descriptor.device);
        Assert.Equal(vif_locking_mode.locked, descriptor.locking_mode);
        Assert.Equal(vif.ipv4_allowed, descriptor.ipv4_allowed);
        Assert.Equal("retained", descriptor.other_config["custom"]);
        Assert.Equal("retained", descriptor.qos_algorithm_params["extra"]);
        Assert.Equal("1024", descriptor.qos_algorithm_params["kbps"]);
        Assert.False(vif.qos_algorithm_params.ContainsKey("kbps"));
        Assert.True(NetworkManagement.VifSettingsChanged(vif, descriptor));
    }

    [Fact]
    public void UnchangedVifAndUnknownQosDoNotCauseReplacement()
    {
        var f = new Fixture();
        var vif = f.AddVif("0");
        vif.qos_algorithm_type = "custom";
        vif.qos_algorithm_params = new Dictionary<string, string> { ["custom"] = "retained" };
        var editor = new VifEditorViewModel(f.Vm, vif, () => { });
        Assert.False(editor.CanEditRate);
        Assert.False(NetworkManagement.VifSettingsChanged(vif, NetworkManagement.PlanVif(f.Vm, editor.Request)));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("ff:ff:ff:ff:ff:ff")]
    [InlineData("01:00:00:00:00:01")]
    [InlineData("00:00:00:00:00:00")]
    public void InvalidAndMulticastMacsAreRejected(string mac)
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanVif(f.Vm, f.NewVif with { Mac = mac }));
    }

    [Fact]
    public void DuplicateMacInvalidRateAndMissingTargetsAreRejected()
    {
        var f = new Fixture();
        f.AddVif("0").MAC = "02:00:00:00:00:01";
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanVif(f.Vm, f.NewVif with { Mac = "02:00:00:00:00:01" }));
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanVif(f.Vm, f.NewVif with { Limit = true, Rate = "0" }));
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanVif(f.Vm, f.NewVif with { Reference = "removed" }));
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanVif(f.Vm, f.NewVif with { NetworkReference = "removed" }));
        Assert.Throws<InvalidOperationException>(() => NetworkManagement.PlanNetwork(f.Connection, f.EditNetwork with { Reference = "removed" }));
    }

    [Fact]
    public void VifControlsRespectVmStateAndAllowedOperations()
    {
        var f = new Fixture();
        var vif = f.AddVif("0");
        Assert.Null(NetworkManagement.VifError(vif));
        Assert.NotNull(NetworkManagement.VifError(vif, true));
        f.Vm.power_state = vm_power_state.Running;
        vif.allowed_operations = [vif_operations.plug];
        Assert.Null(NetworkManagement.VifError(vif, true));
        vif.currently_attached = true;
        Assert.NotNull(NetworkManagement.VifError(vif));
        vif.allowed_operations = [vif_operations.unplug];
        Assert.Null(NetworkManagement.VifError(vif, true));
        f.Vm.power_state = vm_power_state.Suspended;
        Assert.NotNull(NetworkManagement.VifError(vif));
        f.Vm.power_state = vm_power_state.Halted;
        f.Vm.is_control_domain = true;
        Assert.NotNull(NetworkManagement.VifError(vif));
    }

    [Fact]
    public void HiddenAndSystemNetworksAreExcludedFromVmChoices()
    {
        var f = new Fixture();
        var system = f.Add("system", new Network { other_config = new() { ["is_guest_installer_network"] = "true" } });
        var hidden = f.Add("hidden", new Network { other_config = new() { ["HideFromXenCenter"] = "true" } });
        Assert.DoesNotContain(NetworkManagement.VmNetworks(f.Vm), n => n == system || n == hidden);
        Assert.NotNull(NetworkManagement.EditNetworkError(system));
    }

    [Fact]
    public void RowsRetainOriginalTargetAndReflectActionPolicy()
    {
        var f = new Fixture();
        var row = new NetworkItemRow("VLAN 10", "", "", "", "", f.Vlan, "production");
        Assert.True(row.HasActions);
        Assert.True(row.CanRemove);
        Assert.True(row.HasTags);
        f.AddVif("0");
        Assert.False(row.CanRemove);
        Assert.Same(f.Vlan, row.Target);
        Assert.False(new NetworkItemRow("management", "", "", "", "").HasActions);
    }

    private sealed class Fixture
    {
        public XenConnection Connection { get; } = new();
        public Host Host { get; }
        public PIF Uplink { get; }
        public Network Vlan { get; }
        public PIF VlanPif { get; }
        public VM Vm { get; }
        public NetworkEdit NewNetwork => new(null, "New VLAN", "", "", true, "1500", true, "eth0", "42");
        public NetworkEdit EditNetwork => new(Vlan.opaque_ref, Vlan.name_label, "", "", true, "1500", true, "eth0", "10");
        public VifEdit NewVif => new(null, Vlan.opaque_ref, "", false, "");

        public Fixture()
        {
            Host = Add("host", new Host { name_label = "Host", software_version = new() { ["network_backend"] = "openvswitch" } });
            Add("pool", new Pool { master = new("host") });
            Add("physical", new Network { name_label = "Physical", MTU = 1500, PIFs = [new("uplink")] });
            Uplink = Add("uplink", new PIF { host = new("host"), network = new("physical"), device = "eth0", physical = true, managed = true, VLAN = -1 });
            Vlan = Add("vlan", new Network { name_label = "VLAN 10", MTU = 1500, PIFs = [new("vlan-pif")] });
            VlanPif = Add("vlan-pif", new PIF { host = new("host"), network = new("vlan"), device = "eth0", managed = true, VLAN = 10 });
            Vm = Add("vm", new VM { name_label = "VM", power_state = vm_power_state.Halted });
        }

        public VIF AddVif(string device)
        {
            var reference = "vif-" + device;
            var vif = Add(reference, new VIF { VM = new("vm"), network = new("vlan"), device = device });
            Vm.VIFs = [.. Vm.VIFs, new(reference)];
            Vlan.VIFs = [.. Vlan.VIFs, new(reference)];
            return vif;
        }

        public T Add<T>(string reference, T value) where T : XenObject<T>
        {
            Connection.Cache.UpdateFrom(Connection, [new ObjectChange(typeof(T), reference, value)]);
            return Connection.Resolve(new XenRef<T>(reference));
        }
    }
}
