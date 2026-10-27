using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class SummaryAllocationTests
{
    [Fact]
    public void SelectedVmSummariesDoNotAllocateCopiesOfUnrelatedInventory()
    {
        using var server = new JsonRpcTransportTests.RpcServer();
        using var connection = HeartbeatLifecycleTests.Connection(server);
        connection.Cache.UpdateFrom(connection,
            [new ObjectChange(typeof(VM), "selected", new VM { name_label = "Selected guest" })]);
        var node = new InfraTreeNode
        {
            Kind = InfraNodeKind.Vm, OpaqueRef = "selected",
            Server = new ServerNode { Connection = connection }
        };

        BuildSummaries(node);
        var small = AllocatedBytes(node);
        connection.Cache.UpdateFrom(connection, Enumerable.Range(0, 5000)
            .Select(i => new ObjectChange(typeof(VM), $"other-{i}", new VM { name_label = $"Other guest {i}" })).ToArray());
        BuildSummaries(node);
        var large = AllocatedBytes(node);

        // Allow incidental fixed-size allocations, but not even one 5,000-VM array.
        Assert.True(large <= small + 8192, $"Summary allocations grew from {small} to {large} bytes.");
        Assert.Equal("Selected guest", GeneralSummaryBuilder.Build(node).Single(row => row.Label == "Name").Value);
        Assert.Empty(server.Requests);
    }

    private static long AllocatedBytes(InfraTreeNode node)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        BuildSummaries(node);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void BuildSummaries(InfraTreeNode node)
    {
        GeneralSummaryBuilder.Build(node);
        StorageSummaryBuilder.Build(node);
        NetworkSummaryBuilder.Build(node);
        ConsoleSummaryBuilder.Build(node);
    }
}
