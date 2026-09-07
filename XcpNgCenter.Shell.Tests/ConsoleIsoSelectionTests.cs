using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ConsoleIsoSelectionTests
{
    [Fact]
    public void InventoryRefreshPreservesUnappliedChoiceUsingFreshOption()
    {
        var selector = new ConsoleIsoSelection();
        var vm = new VM { opaque_ref = "vm", Connection = new XenConnection() };
        var attached = Iso("attached");
        var desired = Iso("desired");
        selector.Refresh(vm, null, [attached, desired], attached.Vdi);
        var freshDesired = Iso("desired");

        var selected = selector.Refresh(vm, desired, [Iso("attached"), freshDesired], attached.Vdi);

        Assert.Same(freshDesired, selected);
    }

    [Fact]
    public void RemovedIsoFallsBackToAttachedDisk()
    {
        var selector = new ConsoleIsoSelection();
        var vm = new VM { opaque_ref = "vm" };
        var attached = Iso("attached");
        var removed = Iso("removed");
        selector.Refresh(vm, null, [attached, removed], attached.Vdi);
        Assert.Same(attached, selector.Refresh(vm, removed, [attached], attached.Vdi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingVmOrConnectionResetsPendingChoice(bool changeConnection)
    {
        var selector = new ConsoleIsoSelection();
        var connection = new XenConnection();
        var first = new VM { opaque_ref = "vm1", Connection = connection };
        var next = new VM
        {
            opaque_ref = changeConnection ? "vm1" : "vm2",
            Connection = changeConnection ? new XenConnection() : connection
        };
        var attached = Iso("attached");
        var pending = Iso("pending");
        selector.Refresh(first, null, [attached, pending], null);
        Assert.Same(attached, selector.Refresh(next, pending, [attached, pending], attached.Vdi));
    }

    private static IsoOption Iso(string id) => new(id, "library", new VDI { opaque_ref = id });
}
