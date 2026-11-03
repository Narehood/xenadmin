using System.Reflection;
using Newtonsoft.Json.Linq;
using XcpNgCenter.Shell.Services;
using XenAdmin;
using XenAdmin.Actions.VMActions;
using XenAdmin.Core;
using XenAPI;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

[Collection("Action session ownership")]
public sealed class NewVmCreationMediaTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CustomTemplateOnlyGetsADvdDriveWhenMediaIsExplicitlySelected(bool selectMedia)
    {
        var previous = XenAdminConfigManager.Provider;
        var path = Path.Combine(Path.GetTempPath(), "new-vm-media-" + Guid.NewGuid().ToString("N"));
        using var settings = new ShellAppSettings(path);
        XenAdminConfigManager.Provider = new ShellConfigProvider(settings);
        using var server = new JsonRpcTransportTests.RpcServer();
        using var connection = HeartbeatLifecycleTests.Connection(server);
        using var transport = connection.Session.JsonRpcClient;
        try
        {
            connection.Cache.UpdateFrom(connection, [
                new ObjectChange(typeof(VM), "template", new VM { uuid = "template-uuid", is_a_template = true, name_label = "Custom template" }),
                new ObjectChange(typeof(VM), "new-vm", new VM { uuid = "new-vm-uuid" }),
                new ObjectChange(typeof(VBD), "new-cd", new VBD { uuid = "new-cd-uuid", VM = new("new-vm"), type = vbd_type.CD, empty = true })]);
            var action = new CreateVMAction(connection, connection.Resolve(new XenRef<VM>("template")), null!, "new VM", "",
                selectMedia ? InstallMethod.CD : InstallMethod.None, "", new VDI { opaque_ref = "iso" }, "", VmBootMode.Bios,
                null!, 2, 2, 1L << 30, 1L << 30, 1L << 30, [], null!, [], false, false, (_, _) => { }, (_, _) => { }, null!, false, 1, "")
                { Session = connection.Session, VM = connection.Resolve(new XenRef<VM>("new-vm")) };
            // Seed cancellation's borrowed session so the probe never logs in again.
            typeof(XenAdmin.Actions.CancellingAction).GetField("BorrowedSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(action, connection.Session);
            JObject? drive = null;
            server.ResultForRequest = request =>
            {
                var method = request["method"]!.Value<string>();
                if (method == "VM.get_allowed_VBD_devices") return new JArray("0", "3");
                if (method == "Async.VBD.create") { drive = (JObject)request["params"]![1]!; return new JValue("create-task"); }
                if (method == "task.get_record") return new XenAPI.Task
                    { uuid = "task-uuid", status = task_status_type.success, result = "<value>new-cd</value>" }.ToJObject();
                if (method == "task.get_allowed_operations") return new JArray();
                if (method == "Async.VBD.insert") return new JValue("insert-task");
                return new JValue("ok");
            };
            typeof(CreateVMAction).GetMethod("AddCdDrive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(action, null);
            if (!selectMedia) { Assert.Empty(server.Requests); return; }
            Assert.NotNull(drive);
            Assert.Equal("RO", drive["mode"]!.Value<string>());
            Assert.True(drive["bootable"]!.Value<bool>());
            Assert.Equal("3", drive["userdevice"]!.Value<string>());
            var insert = Assert.Single(server.Requests, r => r["method"]!.Value<string>() == "Async.VBD.insert");
            Assert.Equal("new-cd", insert["params"]![1]!.Value<string>());
            Assert.Equal("iso", insert["params"]![2]!.Value<string>());
        }
        finally
        {
            XenAdminConfigManager.Provider = previous;
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
