using System.Reflection;
using System.Runtime.CompilerServices;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using XenAdmin.Actions;
using XenAdmin.Core;
using XenAPI;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests;

public sealed class ReviewSnapshotTests
{
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public async Task SnapshotCommandsWaitForReviewAndRejectCancelledOrChangedTargets(bool revert, bool accept, bool change)
    {
        using var server = new JsonRpcTransportTests.RpcServer();
        using var connection = HeartbeatLifecycleTests.Connection(server);
        connection.Cache.UpdateFrom(connection, [
            new ObjectChange(typeof(VM), "parent", new VM { uuid = "parent-uuid", name_label = "Guest" }),
            new ObjectChange(typeof(VM), "snapshot", new VM { uuid = "snapshot-uuid", name_label = "Restore point",
                is_a_snapshot = true, snapshot_of = new("parent"), allowed_operations = [vm_operations.revert, vm_operations.destroy] })]);
        var main = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        typeof(MainViewModel).GetField("_selectedVm", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, connection.Resolve(new XenRef<VM>("parent")));
        var decision = new TaskCompletionSource<bool>();
        ShellConfirmRequest? prompt = null;
        AsyncAction? queued = null;
        main.ConfirmSnapshot = request => { prompt = request; return decision.Task; };
        main.QueueSnapshotAction = action => queued = action;
        var row = new SnapshotItemRow("Restore point", "", "", "snapshot");
        var command = revert ? main.RevertSnapshotCommand : main.DeleteSnapshotCommand;
        var execution = command.ExecuteAsync(row);
        Assert.NotNull(prompt);
        Assert.Contains("Guest", prompt.Message);
        Assert.Contains("Restore point", prompt.Message);
        Assert.Null(queued);
        Assert.False(execution.IsCompleted);
        if (change) connection.Resolve(new XenRef<VM>("snapshot")).uuid = "replacement-uuid";
        decision.SetResult(accept);
        await execution;
        if (accept && !change)
        {
            Assert.NotNull(queued);
            if (revert) Assert.IsType<VMSnapshotRevertAction>(queued);
            else Assert.IsType<VMSnapshotDeleteAction>(queued);
        }
        else Assert.Null(queued);
        Assert.Empty(server.Requests);
    }
}
