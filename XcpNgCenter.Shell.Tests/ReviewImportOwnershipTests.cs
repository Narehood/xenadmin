using System.Reflection;
using Newtonsoft.Json.Linq;
using XenAdmin.Actions.OvfActions;
using XenAdmin.Core;
using XenAPI;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ReviewImportOwnershipTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FailedImportDestroysOnlyDisksCreatedByThisImport(bool createNew, bool failUpload)
    {
        using var server = new JsonRpcTransportTests.RpcServer();
        using var connection = HeartbeatLifecycleTests.Connection(server);
        var root = Path.Combine(Path.GetTempPath(), "review-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "disk.iso"), new byte[64]);
            connection.Cache.UpdateFrom(connection, [
                new ObjectChange(typeof(SR), "sr", new SR { uuid = "sr-uuid", physical_size = 1024 }),
                new ObjectChange(typeof(VDI), "existing", new VDI { uuid = "existing-uuid", virtual_size = failUpload ? 1024 : 16,
                    allowed_operations = [vdi_operations.destroy] })]);
            server.ResultForRequest = request =>
            {
                string? method = request["method"]?.Value<string>();
                server.Failure = method == "task.create";
                if (method == "VDI.create")
                {
                    connection.Cache.UpdateFrom(connection, [new ObjectChange(typeof(VDI), "created", new VDI
                        { uuid = "created-uuid", virtual_size = 1024, allowed_operations = [vdi_operations.destroy] })]);
                    return new JValue("created");
                }
                return new JValue("ok");
            };
            var action = new ImportApplianceAction(connection, null!, new(), false, false, null!, false, null!, false);
            Assert.Throws<TargetInvocationException>(() => Invoke(action, "ImportFile", "test", root, "disk.iso", null,
                "sr-uuid", "", createNew ? null : "existing-uuid", false, (Action<float>)(_ => { })));
            var destroys = server.Requests.Where(r => r["method"]?.Value<string>() == "VDI.destroy").ToArray();
            if (createNew) Assert.Equal("created", Assert.Single(destroys)["params"]![1]!.Value<string>());
            else Assert.Empty(destroys);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void VmRollbackPreservesExistingDisksAndIsoAndCleansOwnedUnattachedDisk()
    {
        using var server = new JsonRpcTransportTests.RpcServer();
        using var connection = HeartbeatLifecycleTests.Connection(server);
        connection.Cache.UpdateFrom(connection, [
            new ObjectChange(typeof(VM), "vm", new VM { VBDs = [new("disk-vbd"), new("iso-vbd")] }),
            new ObjectChange(typeof(VBD), "disk-vbd", new VBD { VDI = new("existing"), type = vbd_type.Disk }),
            new ObjectChange(typeof(VBD), "iso-vbd", new VBD { VDI = new("iso"), type = vbd_type.CD }),
            new ObjectChange(typeof(VDI), "existing", new VDI { uuid = "existing-uuid" }),
            new ObjectChange(typeof(VDI), "iso", new VDI { uuid = "iso-uuid", read_only = true })]);
        var action = new ImportApplianceAction(connection, null!, new(), false, false, null!, false, null!, false);
        var owned = (HashSet<string>)typeof(ImportApplianceAction).GetField("createdVdis", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(action)!;
        owned.Add("owned-not-in-cache");
        Invoke(action, "CleanUpVm", new XenRef<VM>("vm"));
        Assert.Equal(new[] { "VM.destroy", "VDI.destroy" }, server.Requests.Select(r => r["method"]!.Value<string>()));
        Assert.Equal("owned-not-in-cache", server.Requests.Last()["params"]![1]!.Value<string>());
        Assert.Empty(owned);
    }

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
