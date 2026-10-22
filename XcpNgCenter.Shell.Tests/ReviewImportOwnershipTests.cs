using System.Reflection;
using Newtonsoft.Json.Linq;
using XenAdmin.Actions.OvfActions;
using XenAdmin.Core;
using XenAPI;
using XenAdmin.Actions.VMActions;
using XenOvf.Definitions;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ReviewImportOwnershipTests
{
    /// <summary>Exercises production import failures and verifies that only VDIs created by that import receive destroy requests.</summary>
    /// <param name="createNew">Whether the importer must allocate a new VDI.</param>
    /// <param name="failUpload">Whether failure occurs after destination capacity validation.</param>
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
                if (method == "VDI.get_allowed_operations") return new JArray("destroy");
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

    /// <summary>Preserves borrowed disks and ISO media while cleaning an owned disk that has no cache entry or attachment.</summary>
    [Fact]
    public void VmRollbackPreservesExistingDisksAndIsoAndCleansOwnedUnattachedDisk()
    {
        using var server = new JsonRpcTransportTests.RpcServer();
        using var connection = HeartbeatLifecycleTests.Connection(server);
        server.ResultForRequest = request => request["method"]?.Value<string>() == "VDI.get_allowed_operations" ? new JArray("destroy") : new JValue("ok");
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
        Assert.Equal(new[] { "VM.destroy", "VDI.get_allowed_operations", "VDI.destroy" }, server.Requests.Select(r => r["method"]!.Value<string>()));
        Assert.Equal("owned-not-in-cache", server.Requests.Last()["params"]![1]!.Value<string>());
        Assert.Empty(owned);
    }

    /// <summary>Keeps an owned VDI tracked across cache loss, a server-side lock and a raced destroy failure until deletion succeeds.</summary>
    [Fact]
    public void CacheLossDoesNotReleaseServerLockAndBusyDestroyIsRetried()
    {
        using var server = new JsonRpcTransportTests.RpcServer();
        using var connection = HeartbeatLifecycleTests.Connection(server);
        var action = new ImportApplianceAction(connection, null!, new(), false, false, null!, false, null!, false);
        var owned = (HashSet<string>)typeof(ImportApplianceAction).GetField("createdVdis", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(action)!;
        owned.Add("owned-missing-cache");
        int checks = 0, destroys = 0;
        server.ResultForRequest = request =>
        {
            server.RawBody = null;
            string? method = request["method"]?.Value<string>();
            if (method == "VDI.get_allowed_operations") return ++checks == 1 ? new JArray() : new JArray("destroy");
            if (method == "VDI.destroy" && ++destroys == 1)
                server.RawBody = new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"], ["error"] = new JObject
                    { ["code"] = 1, ["message"] = "OTHER_OPERATION_IN_PROGRESS", ["data"] = new JArray("VDI", "owned-missing-cache") } }.ToString();
            return new JValue("ok");
        };
        Invoke(action, "CleanUpVm", new XenRef<VM>("partial-vm"));
        Assert.Equal(3, checks);
        Assert.Equal(2, destroys);
        Assert.Empty(owned);
    }

    /// <summary>Builds a CD VBD through the importer and verifies both rollback and the client owned-disk deletion path preserve a reused ISO.</summary>
    [Fact]
    public void ProductionCdAttachmentDoesNotOwnOrDeleteExistingLibraryIso()
    {
        using var server = new JsonRpcTransportTests.RpcServer();
        using var connection = HeartbeatLifecycleTests.Connection(server);
        connection.Cache.UpdateFrom(connection, [
            new ObjectChange(typeof(VM), "vm", new VM { uuid = "vm-uuid" }),
            new ObjectChange(typeof(VDI), "iso", new VDI { uuid = "iso-uuid", name_label = "library.iso", read_only = true })]);
        JObject? record = null;
        server.ResultForRequest = request =>
        {
            string? method = request["method"]?.Value<string>();
            if (method == "VM.get_allowed_VBD_devices") return new JArray("3");
            if (method == "VBD.create")
            {
                record = (JObject)request["params"]![1]!;
                connection.Cache.UpdateFrom(connection, [new ObjectChange(typeof(VBD), "cd-vbd", new VBD
                    { VM = new("vm"), VDI = new("iso"), type = vbd_type.CD, other_config = record["other_config"]!.ToObject<Dictionary<string, string>>()! })]);
                connection.Resolve(new XenRef<VM>("vm")).VBDs = [new("cd-vbd")];
                return new JValue("cd-vbd");
            }
            return new JValue("ok");
        };
        var env = new EnvelopeType { References = new References_Type { File = [new File_Type { id = "file", href = "library.iso" }] },
            Sections = [new DiskSection_Type { Disk = [new VirtualDiskDesc_Type { diskId = "cd", fileRef = "file" }] }] };
        var rasd = new RASD_Type { ResourceType = new ResourceType { Value = 16 }, HostResource = [new cimString("ovf:/disk/cd")] };
        var action = new ImportApplianceAction(connection, null!, new(), false, false, null!, false, null!, false);
        Invoke(action, "AddResourceSettingData", env, new XenRef<VM>("vm"), rasd, "", 0, (Action<float>)(_ => { }));
        Assert.NotNull(record);
        Assert.Null(record["other_config"]!["owner"]);
        Invoke(action, "CleanUpVm", new XenRef<VM>("vm"));
        VMDestroyAction.DestroyVM(connection.Session, connection.Resolve(new XenRef<VM>("vm")), deleteAllOwnerDisks: true);
        Assert.DoesNotContain(server.Requests, request => request["method"]?.Value<string>() == "VDI.destroy");
    }

    /// <summary>Invokes a production import boundary without starting the full appliance wizard or contacting a real pool.</summary>
    /// <param name="target">The candidate redirected URI.</param>
    /// <param name="name">The private production method or payload name selected by the fixture.</param>
    /// <param name="args">Arguments for the reflected production boundary.</param>
    /// <returns>The reflected production result.</returns>
    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
