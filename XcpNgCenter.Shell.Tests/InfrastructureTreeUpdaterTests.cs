using System.Collections.Specialized;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public class InfrastructureTreeUpdaterTests
{
    private readonly ServerNode _server = new();
    private InfraTreeNode Node(InfraNodeKind kind, string? reference, string title, params InfraTreeNode[] children)
    {
        var node = new InfraTreeNode { Kind = kind, OpaqueRef = reference, Title = title, Server = _server };
        foreach (var child in children) node.Children.Add(child);
        return node;
    }

    [Fact]
    public void RoutineMetadataRefreshRetainsNodesExpansionAndCollections()
    {
        var vm = Node(InfraNodeKind.Vm, "vm", "Old name");
        var host = Node(InfraNodeKind.Host, "host", "Host", vm);
        host.IsExpanded = false;
        var root = Node(InfraNodeKind.Pool, "pool", "Pool", host);
        var collectionChanges = 0;
        root.Children.CollectionChanged += (_, _) => collectionChanges++;
        host.Children.CollectionChanged += (_, _) => collectionChanges++;
        var incomingVm = Node(InfraNodeKind.Vm, "vm", "New name");
        incomingVm.Detail = "Paused";
        incomingVm.StatusTooltip = "VM paused";
        var result = InfrastructureTreeUpdater.Apply(root,
            Node(InfraNodeKind.Pool, "pool", "Pool", Node(InfraNodeKind.Host, "host", "Host", incomingVm)));
        Assert.Same(root, result);
        Assert.Same(host, Assert.Single(root.Children));
        Assert.Same(vm, Assert.Single(host.Children));
        Assert.False(host.IsExpanded);
        Assert.Equal("New name", vm.Title);
        Assert.Equal("Paused", vm.Detail);
        Assert.Equal("VM paused", vm.StatusTooltip);
        Assert.Equal(0, collectionChanges);
    }

    [Fact]
    public void ReorderingUsesCollectionMovesAndAddsRemovesOnlyChangedObjects()
    {
        var a = Node(InfraNodeKind.Vm, "a", "A");
        var b = Node(InfraNodeKind.Vm, "b", "B");
        var removed = Node(InfraNodeKind.Vm, "removed", "Removed");
        var root = Node(InfraNodeKind.Host, "host", "Host", a, b, removed);
        var actions = new List<NotifyCollectionChangedAction>();
        root.Children.CollectionChanged += (_, e) => actions.Add(e.Action);
        InfrastructureTreeUpdater.Apply(root, Node(InfraNodeKind.Host, "host", "Host",
            Node(InfraNodeKind.Vm, "b", "First"), Node(InfraNodeKind.Vm, "a", "Second"), Node(InfraNodeKind.Vm, "new", "New")));
        Assert.Equal(new[] { "b", "a", "new" }, root.Children.Select(n => n.OpaqueRef));
        Assert.Same(b, root.Children[0]);
        Assert.Same(a, root.Children[1]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Move, NotifyCollectionChangedAction.Add }, actions);
    }

    [Fact]
    public void MigrationRetainsVmAndDetachesItsOldParentBeforeAddingToNewParent()
    {
        var vm = Node(InfraNodeKind.Vm, "vm", "VM");
        var first = Node(InfraNodeKind.Host, "first", "First");
        var second = Node(InfraNodeKind.Host, "second", "Second", vm);
        var root = Node(InfraNodeKind.Pool, "pool", "Pool", first, second);
        first.Children.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add) Assert.DoesNotContain(vm, second.Children);
        };
        InfrastructureTreeUpdater.Apply(root, Node(InfraNodeKind.Pool, "pool", "Pool",
            Node(InfraNodeKind.Host, "first", "First", Node(InfraNodeKind.Vm, "vm", "VM")),
            Node(InfraNodeKind.Host, "second", "Second")));
        Assert.Same(vm, Assert.Single(first.Children));
        Assert.Empty(second.Children);
    }

    [Fact]
    public void NewUnplacedGroupReusesVmFromARemovedHost()
    {
        var vm = Node(InfraNodeKind.Vm, "vm", "VM");
        var root = Node(InfraNodeKind.Pool, "pool", "Pool", Node(InfraNodeKind.Host, "gone", "Gone", vm));
        InfrastructureTreeUpdater.Apply(root, Node(InfraNodeKind.Pool, "pool", "Pool",
            Node(InfraNodeKind.Group, null, "Other VMs", Node(InfraNodeKind.Vm, "vm", "VM"))));
        Assert.Equal(InfraNodeKind.Group, Assert.Single(root.Children).Kind);
        Assert.Same(vm, Assert.Single(root.Children[0].Children));
    }

    [Fact]
    public void ReferenceKindAndServerChangesReplaceTheRoot()
    {
        var existing = Node(InfraNodeKind.Host, "host", "Host");
        var pool = Node(InfraNodeKind.Pool, "pool", "Pool");
        Assert.Same(pool, InfrastructureTreeUpdater.Apply(existing, pool));
        var replacement = Node(InfraNodeKind.Host, "replacement", "Host");
        Assert.Same(replacement, InfrastructureTreeUpdater.Apply(existing, replacement));
        var anotherServer = new InfraTreeNode { Server = new ServerNode(), Kind = InfraNodeKind.Host, OpaqueRef = "host" };
        Assert.Same(anotherServer, InfrastructureTreeUpdater.Apply(existing, anotherServer));
    }
}
