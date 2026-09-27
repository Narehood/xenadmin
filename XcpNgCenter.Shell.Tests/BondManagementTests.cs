using XenAdmin.Network;
using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;
using Network = XenAPI.Network;

namespace XcpNgCenter.Shell.Tests;

public sealed class BondManagementTests
{
    [Fact]
    public void CreateChecksAllHostsAndDoesNotLockOrMutateInventory()
    {
        var f = new Fixture();
        var originalCount = f.Connection.Cache.Networks.Length;
        var plan = BondManagement.PlanCreate(f.Connection, f.Create);
        Assert.Equal(new[] { "host0-eth1", "host0-eth2" }, plan.CoordinatorMembers.Select(p => p.opaque_ref));
        Assert.Equal(1500, plan.Mtu);
        Assert.Contains("host1-eth2", plan.Snapshot);
        Assert.All(f.Connection.Cache.PIFs, p => Assert.False(p.Locked));
        Assert.Equal(originalCount, f.Connection.Cache.Networks.Length);
    }

    [Theory]
    [InlineData("management")]
    [InlineData("ipv4")]
    [InlineData("ipv6")]
    [InlineData("locked")]
    [InlineData("disallow-unplug")]
    [InlineData("cluster")]
    [InlineData("vlan")]
    [InlineData("tunnel")]
    [InlineData("sriov")]
    [InlineData("unmanaged")]
    [InlineData("vm")]
    public void CreateRejectsProtectedOrUsedNicOnAnotherHost(string reason)
    {
        var f = new Fixture();
        f.Protect(f.Members[1][0], reason);
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanCreate(f.Connection, f.Create));
        Assert.NotNull(BondManagement.CandidateError(f.Members[0][0]));
    }

    [Theory]
    [InlineData("575")]
    [InlineData("10000")]
    [InlineData("9000")]
    [InlineData("not-an-mtu")]
    public void CreateRejectsInvalidOrUnsupportedMtu(string mtu)
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanCreate(f.Connection, f.Create with { Mtu = mtu }));
    }

    [Fact]
    public void CreateRequiresDistinctCoordinatorNicsAndCompletePoolInventory()
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanCreate(f.Connection,
            f.Create with { MemberReferences = [f.Members[0][0].opaque_ref, f.Members[0][0].opaque_ref] }));
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanCreate(f.Connection,
            f.Create with { MemberReferences = [f.Members[1][0].opaque_ref, f.Members[1][1].opaque_ref] }));
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanCreate(f.Connection,
            f.Create with { MemberReferences = ["removed", f.Members[0][1].opaque_ref] }));
        f.Hosts[1].PIFs = [new(f.Members[1][0].opaque_ref)];
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanCreate(f.Connection, f.Create));
    }

    [Fact]
    public void CreationRejectsOfflineBusyHostsAndLacpOnBridge()
    {
        var f = new Fixture();
        f.Hosts[1].software_version = new() { ["network_backend"] = "bridge" };
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanCreate(f.Connection, f.Create with { Mode = BondManagement.Modes[2] }));
        Assert.NotNull(BondManagement.PlanCreate(f.Connection, f.Create));
        f.Connection.Resolve(f.Hosts[1].metrics).live = false;
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanCreate(f.Connection, f.Create));
        f.Connection.Resolve(f.Hosts[1].metrics).live = true;
        f.Hosts[1].Locked = true;
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanCreate(f.Connection, f.Create));
    }

    [Fact]
    public void CreateRejectsHostInterfaceListPointingAtAnotherHostsNics()
    {
        var f = new Fixture();
        // The shared action groups NICs through host.PIFs. An inconsistent
        // reverse list must not send the coordinator's NICs twice to Bond.create.
        f.Hosts[1].PIFs = f.Members[0].Select(p => new XenRef<PIF>(p.opaque_ref)).ToList();
        var error = Assert.Throws<InvalidOperationException>(() => BondManagement.PlanCreate(f.Connection, f.Create));
        Assert.Contains("inconsistent host membership", error.Message);
        Assert.NotNull(BondManagement.CandidateError(f.Members[0][0]));
    }

    [Fact]
    public void ExistingPlanRetainsCoordinatorBondAndSamePhysicalMembersAcrossPool()
    {
        var f = new Fixture();
        var network = f.CreateExistingBond();
        var plan = BondManagement.PlanExisting(f.Connection, network.opaque_ref, BondManagement.Modes[2]);
        Assert.Same(network, plan.Network);
        Assert.Equal(new[] { "bond0", "bond1" }, plan.Bonds.Select(b => b.opaque_ref));
        Assert.False(network.Locked);
        Assert.All(plan.Bonds, b => Assert.False(b.Locked));
        var row = new NetworkItemRow("Bond", "", "", "", "", network);
        Assert.True(row.IsBond);
        Assert.True(row.CanManageBond);
        Assert.False(row.CanRemove); // Generic VLAN/private removal must not destroy a bond.
    }

    [Theory]
    [InlineData("management")]
    [InlineData("ipv4")]
    [InlineData("ipv6")]
    [InlineData("cluster")]
    [InlineData("vlan")]
    [InlineData("tunnel")]
    [InlineData("sriov")]
    [InlineData("vm")]
    public void EditAndRemovalRejectProtectedBondMasters(string reason)
    {
        var f = new Fixture();
        var network = f.CreateExistingBond();
        f.Protect(f.Connection.Resolve(new XenRef<PIF>("bond-pif1")), reason);
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanExisting(f.Connection, network.opaque_ref));
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanExisting(f.Connection, network.opaque_ref, BondManagement.Modes[1]));
        Assert.NotNull(BondManagement.ExistingError(network));
    }

    [Fact]
    public void ExistingBondRequiresCompleteConsistentMembershipAndPrimaryNic()
    {
        var f = new Fixture();
        var network = f.CreateExistingBond();
        var otherBond = f.Connection.Resolve(new XenRef<Bond>("bond1"));
        otherBond.primary_slave = new("missing");
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanExisting(f.Connection, network.opaque_ref));
        otherBond.primary_slave = new(f.Members[1][0].opaque_ref);
        f.Members[1][1].bond_slave_of = new("different-bond");
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanExisting(f.Connection, network.opaque_ref));
        f.Members[1][1].bond_slave_of = new(otherBond.opaque_ref);
        f.Members[1][1].device = "eth3";
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanExisting(f.Connection, network.opaque_ref));
    }

    [Fact]
    public void ExistingBondRejectsMasterWithUnresolvedBondSlaveReference()
    {
        var f = new Fixture();
        var network = f.CreateExistingBond();
        f.Connection.Resolve(new XenRef<PIF>("bond-pif1")).bond_slave_of = new("unresolved-outer-bond");
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanExisting(f.Connection, network.opaque_ref));
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanExisting(f.Connection, network.opaque_ref, BondManagement.Modes[1]));
        Assert.NotNull(BondManagement.ExistingError(network));
    }

    [Fact]
    public void ConfirmationSnapshotDetectsConcurrentModeAndMembershipChanges()
    {
        var f = new Fixture();
        var create = BondManagement.PlanCreate(f.Connection, f.Create);
        f.Members[1][0].MTU = 9000;
        Assert.Throws<InvalidOperationException>(() => BondManagement.RequireUnchanged(create.Snapshot,
            BondManagement.PlanCreate(f.Connection, f.Create).Snapshot));

        var network = f.CreateExistingBond();
        var before = BondManagement.PlanExisting(f.Connection, network.opaque_ref);
        f.Connection.Resolve(new XenRef<Bond>("bond1")).mode = bond_mode.balance_slb;
        var after = BondManagement.PlanExisting(f.Connection, network.opaque_ref);
        Assert.Throws<InvalidOperationException>(() => BondManagement.RequireUnchanged(before.Snapshot, after.Snapshot));
        BondManagement.RequireUnchanged(after.Snapshot, after.Snapshot);
        f.Members[1][0].management = true;
        Assert.Throws<InvalidOperationException>(() => BondManagement.PlanExisting(f.Connection, network.opaque_ref));
    }

    [Fact]
    public void EditorRetainsSelectionAndReportsBusyMembersWithoutCreatingActions()
    {
        var f = new Fixture();
        f.Members[1][0].management = true;
        var editor = new BondEditorViewModel(f.Connection, null, () => { });
        Assert.Equal(2, editor.Members.Count);
        Assert.False(editor.Members[0].IsAvailable);
        Assert.True(editor.Members[1].IsAvailable);
        editor.Members[1].IsSelected = true;
        editor.SelectedMode = BondManagement.Modes[2];
        Assert.True(editor.IsLacp);
        Assert.True(editor.Members[1].IsSelected);
        Assert.All(f.Connection.Cache.PIFs, p => Assert.False(p.Locked));
    }

    [Theory]
    [InlineData(bond_mode.unknown, null, "unknown")]
    [InlineData(bond_mode.lacp, "future_hash", "future_hash")]
    [InlineData(bond_mode.lacp, null, "not reported")]
    public async System.Threading.Tasks.Task UnknownExistingModeRequiresExplicitSupportedChoice(bond_mode mode, string? hashing, string reported)
    {
        var f = new Fixture();
        var network = f.CreateExistingBond();
        foreach (var bond in f.Connection.Cache.Bonds)
        {
            bond.mode = mode;
            bond.properties = hashing == null ? [] : new() { ["hashing_algorithm"] = hashing };
        }
        var editor = new BondEditorViewModel(f.Connection, network, () => throw new InvalidOperationException("Unexpected close"));
        Assert.Null(editor.SelectedMode);
        Assert.False(editor.CanSave);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Contains(reported, editor.CurrentModeNotice);
        Assert.Contains("explicitly", editor.CurrentModeNotice);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Empty(editor.StatusMessage);
        Assert.False(editor.IsSaving);

        editor.SelectedMode = BondManagement.Modes[0];
        Assert.True(editor.CanSave);
        Assert.True(editor.SaveCommand.CanExecute(null));
        Assert.Contains(reported, editor.CurrentModeNotice);
        editor.IsSaving = true;
        Assert.False(editor.CanSave);
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.IsSaving = false;
        editor.SelectedMode = null;
        Assert.False(editor.CanSave);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.All(f.Connection.Cache.Bonds, bond => Assert.Equal(mode, bond.mode));
    }

    [Fact]
    public void SupportedExistingModesArePreservedAndNewBondsKeepTheirDefault()
    {
        var f = new Fixture();
        var creating = new BondEditorViewModel(f.Connection, null, () => { });
        Assert.Equal(BondManagement.Modes[0], creating.SelectedMode);
        Assert.Empty(creating.CurrentModeNotice);
        var network = f.CreateExistingBond();
        foreach (var mode in BondManagement.Modes)
        {
            foreach (var bond in f.Connection.Cache.Bonds)
            {
                bond.mode = mode.Mode;
                bond.properties = new() { ["hashing_algorithm"] = Bond.HashingAlgoritmToString(mode.Hashing) };
            }
            var editing = new BondEditorViewModel(f.Connection, network, () => { });
            Assert.Equal(mode, editing.SelectedMode);
            Assert.True(editing.CanSave);
            Assert.True(editing.SaveCommand.CanExecute(null));
            Assert.Contains(mode.Label, editing.CurrentModeNotice);
        }
    }

    [Theory]
    [InlineData(bond_mode.balance_slb, "", "Balance SLB")]
    [InlineData(bond_mode.lacp, "future_hash", "future_hash")]
    public void DifferentOrUnsupportedModeOnAnotherHostRequiresExplicitChoice(bond_mode mode, string hashing, string reported)
    {
        var f = new Fixture();
        var network = f.CreateExistingBond();
        var otherBond = f.Connection.Resolve(new XenRef<Bond>("bond1"));
        otherBond.mode = mode;
        otherBond.properties = new() { ["hashing_algorithm"] = hashing };
        var editor = new BondEditorViewModel(f.Connection, network, () => { });
        Assert.Null(editor.SelectedMode);
        Assert.False(editor.CanSave);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Contains("Active-backup", editor.CurrentModeNotice);
        Assert.Contains(reported, editor.CurrentModeNotice);
        editor.SelectedMode = new BondModeOption("unsupported", bond_mode.unknown, Bond.hashing_algoritm.unknown);
        Assert.False(editor.CanSave);
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.SelectedMode = BondManagement.Modes[0];
        Assert.True(editor.CanSave);
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    private sealed class Fixture
    {
        public XenConnection Connection { get; } = new();
        public Host[] Hosts { get; }
        public PIF[][] Members { get; }
        public BondCreateRequest Create => new("Bond", Members[0].Select(p => p.opaque_ref).ToArray(), "1500", BondManagement.Modes[0], false);

        public Fixture()
        {
            Hosts = Enumerable.Range(0, 2).Select(i =>
            {
                Add("metrics" + i, new Host_metrics { live = true });
                return Add("host" + i, new Host
                {
                    name_label = "Host " + i, enabled = true, metrics = new("metrics" + i),
                    software_version = new() { ["network_backend"] = "openvswitch" }
                });
            }).ToArray();
            Add("pool", new Pool { master = new(Hosts[0].opaque_ref) });
            Members = Hosts.Select((host, i) => Enumerable.Range(1, 2).Select(device => Add($"host{i}-eth{device}", new PIF
            {
                device = "eth" + device, host = new(host.opaque_ref), network = new("network" + device),
                physical = true, managed = true, VLAN = -1, MTU = 1500
            })).ToArray()).ToArray();
            foreach (var host in Hosts) host.PIFs = Connection.Cache.PIFs.Where(p => p.host.opaque_ref == host.opaque_ref).Select(p => new XenRef<PIF>(p.opaque_ref)).ToList();
            foreach (var device in Enumerable.Range(1, 2))
                Add("network" + device, new Network
                {
                    name_label = "NIC " + device, MTU = 1500,
                    PIFs = Members.SelectMany(p => p).Where(p => p.device == "eth" + device).Select(p => new XenRef<PIF>(p.opaque_ref)).ToList()
                });
        }

        public Network CreateExistingBond()
        {
            for (var i = 0; i < Hosts.Length; i++)
            {
                var master = Add("bond-pif" + i, new PIF
                {
                    device = "bond0", host = new(Hosts[i].opaque_ref), network = new("bond-network"),
                    managed = true, VLAN = -1, MTU = 1500, bond_master_of = [new("bond" + i)]
                });
                Hosts[i].PIFs = [.. Hosts[i].PIFs, new(master.opaque_ref)];
                var bond = Add("bond" + i, new Bond
                {
                    master = new(master.opaque_ref), mode = bond_mode.active_backup,
                    slaves = Members[i].Select(p => new XenRef<PIF>(p.opaque_ref)).ToList(),
                    primary_slave = new(Members[i][0].opaque_ref)
                });
                foreach (var member in Members[i]) member.bond_slave_of = new(bond.opaque_ref);
            }
            return Add("bond-network", new Network
            {
                name_label = "Bond", MTU = 1500, PIFs = [new("bond-pif0"), new("bond-pif1")]
            });
        }

        public void Protect(PIF pif, string reason)
        {
            switch (reason)
            {
                case "management": pif.management = true; break;
                case "ipv4": pif.ip_configuration_mode = ip_configuration_mode.Static; break;
                case "ipv6": pif.ipv6_configuration_mode = ipv6_configuration_mode.Autoconf; break;
                case "locked": pif.Locked = true; break;
                case "disallow-unplug": pif.disallow_unplug = true; break;
                case "cluster": Add("cluster-host", new Cluster_host { PIF = new(pif.opaque_ref) }); break;
                case "vlan": pif.VLAN_slave_of = [new("dependent-vlan")]; break;
                case "tunnel": pif.tunnel_transport_PIF_of = [new("dependent-tunnel")]; break;
                case "sriov": pif.sriov_physical_PIF_of = [new("sriov")]; break;
                case "unmanaged": pif.managed = false; break;
                case "vm": Add("stopped-vm-vif", new VIF { network = pif.network, currently_attached = false }); break;
            }
        }

        private T Add<T>(string reference, T value) where T : XenObject<T>
        {
            Connection.Cache.UpdateFrom(Connection, [new ObjectChange(typeof(T), reference, value)]);
            return Connection.Resolve(new XenRef<T>(reference));
        }
    }
}
