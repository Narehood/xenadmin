using Avalonia;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

[CollectionDefinition("Infrastructure rendering", DisableParallelization = true)]
public sealed class InfrastructureRenderingCollection : ICollectionFixture<InfrastructureRenderingFixture>;

public sealed class InfrastructureRenderingFixture
{
    public InfrastructureRenderingFixture()
    {
        // Decode the real status icons without opening a desktop window.
        AppBuilder.Configure<Application>()
            .UseStandardRuntimePlatformSubsystem()
            .UseWindowingSubsystem(() => { }, "Offscreen")
            .UseSkia()
            .SetupWithoutStarting();
    }
}

[Collection("Infrastructure rendering")]
public sealed class InfrastructureTreeBuilderTests
{
    [Fact]
    public void StandaloneHostIsRootWithRunningAndUnassignedStoppedVmsDirectlyBelowIt()
    {
        var (server, conn) = Inventory("", 1);
        Add(conn, "template", new VM { is_a_template = true });
        Add(conn, "snapshot", new VM { is_a_snapshot = true });
        Add(conn, "control-domain", new VM { is_control_domain = true });

        var root = InfrastructureTreeBuilder.Build(server, conn);

        Assert.Equal(InfraNodeKind.Host, root.Kind);
        Assert.Equal("host-1", root.OpaqueRef);
        Assert.Equal("Server 1", root.Title);
        Assert.Equal("192.0.2.1", root.Subtitle);
        Assert.Same(server, root.Server);
        Assert.NotNull(conn.Resolve(new XenRef<Host>(root.OpaqueRef)));
        Assert.Contains("Standalone", root.Detail);
        Assert.True(root.ShowStatusIcon);
        Assert.Collection(root.Children,
            node => AssertVm(node, "running", server),
            node => AssertVm(node, "stopped", server));
    }

    [Fact]
    public void StandaloneHostKeepsLocalAndSharedStorageWithoutDuplicates()
    {
        var (server, conn) = Inventory("", 1);
        AddStorage(conn, "local", shared: false);
        AddStorage(conn, "shared", shared: true);
        Add(conn, "detached", new SR { name_label = "Detached", type = "lvm" });

        var root = InfrastructureTreeBuilder.Build(server, conn);

        Assert.Equal(InfraNodeKind.Host, root.Kind);
        Assert.Equal(new[] { "local", "shared" }, root.Children
            .Where(node => node.Kind == InfraNodeKind.Storage).Select(node => node.OpaqueRef));
        Assert.Equal(4, root.Children.Count);
    }

    [Theory]
    [InlineData("Home cluster", 1)]
    [InlineData("Home cluster", 2)]
    [InlineData("", 2)]
    public void VisiblePoolsRetainHostsOtherVmsAndSharedStorage(string poolName, int hostCount)
    {
        var (server, conn) = Inventory(poolName, hostCount);
        AddStorage(conn, "local", shared: false);
        AddStorage(conn, "shared", shared: true);

        var root = InfrastructureTreeBuilder.Build(server, conn);

        Assert.Equal(InfraNodeKind.Pool, root.Kind);
        Assert.Equal("pool", root.OpaqueRef);
        if (poolName.Length > 0) Assert.Equal(poolName, root.Title);
        var hosts = root.Children.Where(node => node.Kind == InfraNodeKind.Host).ToList();
        Assert.Equal(hostCount, hosts.Count);
        Assert.Equal("host-1", hosts[0].OpaqueRef);
        Assert.Collection(hosts[0].Children,
            node => AssertVm(node, "running", server),
            node => Assert.Equal("local", node.OpaqueRef));
        var group = Assert.Single(root.Children, node => node.Kind == InfraNodeKind.Group);
        AssertVm(Assert.Single(group.Children), "stopped", server);
        Assert.Equal("shared", Assert.Single(root.Children, node => node.Kind == InfraNodeKind.Storage).OpaqueRef);
    }

    [Fact]
    public void NamingPoolChangesLayoutAndKeepsHostAndVmIdentity()
    {
        var (server, conn) = Inventory("", 1);
        var standalone = InfrastructureTreeBuilder.Build(server, conn);
        conn.Cache.Pools[0].name_label = "New pool";

        var pool = InfrastructureTreeBuilder.Build(server, conn);
        var host = Assert.Single(pool.Children, node => node.Kind == InfraNodeKind.Host);

        Assert.Equal(InfraNodeKind.Pool, pool.Kind);
        Assert.Equal(standalone.OpaqueRef, host.OpaqueRef);
        Assert.Equal(standalone.Children[0].OpaqueRef, host.Children[0].OpaqueRef);
        Assert.Same(standalone.Server, host.Server);
        conn.Cache.Pools[0].name_label = "";
        Assert.Equal(InfraNodeKind.Host, InfrastructureTreeBuilder.Build(server, conn).Kind);
    }

    [Fact]
    public void EmptyCacheDoesNotInventAHost()
    {
        var conn = new XenConnection { Hostname = "192.0.2.1" };
        var root = InfrastructureTreeBuilder.Build(new ServerNode { Connection = conn }, conn);

        Assert.Empty(root.Children);
        Assert.Equal("192.0.2.1", root.Title);
    }

    private static (ServerNode Server, XenConnection Connection) Inventory(string poolName, int hostCount)
    {
        var conn = new XenConnection { Hostname = "192.0.2.1" };
        Add(conn, "pool", new Pool { name_label = poolName, master = new XenRef<Host>("host-1") });
        for (var i = 1; i <= hostCount; i++)
            Add(conn, $"host-{i}", new Host { name_label = $"Server {i}", address = $"192.0.2.{i}" });
        Add(conn, "running", new VM
        {
            name_label = "Running VM", power_state = vm_power_state.Running,
            resident_on = new XenRef<Host>("host-1")
        });
        Add(conn, "stopped", new VM { name_label = "Stopped VM", power_state = vm_power_state.Halted });
        return (new ServerNode { Connection = conn, IsConnected = true }, conn);
    }

    private static void AddStorage(XenConnection conn, string reference, bool shared)
    {
        Add(conn, reference, new SR
        {
            name_label = reference, type = "lvm", shared = shared,
            PBDs = [new XenRef<PBD>(reference + "-pbd")]
        });
        Add(conn, reference + "-pbd", new PBD
        {
            SR = new XenRef<SR>(reference), host = new XenRef<Host>("host-1"), currently_attached = true
        });
    }

    private static void Add<T>(XenConnection conn, string reference, T value) where T : XenObject<T> =>
        conn.Cache.UpdateFrom(conn, [new ObjectChange(typeof(T), reference, value)]);

    private static void AssertVm(InfraTreeNode node, string reference, ServerNode server)
    {
        Assert.Equal(InfraNodeKind.Vm, node.Kind);
        Assert.Equal(reference, node.OpaqueRef);
        Assert.Same(server, node.Server);
    }
}
