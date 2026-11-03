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
    [InlineData(true, false, false, "c", "dc")]
    [InlineData(true, false, false, "cn", "dcn")]
    [InlineData(true, false, false, "dc", "dc")]
    [InlineData(true, true, false, "cn", "cn")]
    [InlineData(true, false, true, "dc", "dc")]
    [InlineData(false, false, false, "c", "c")]
    public void CustomTemplateMediaUsesBootableDvdSlots(bool selectMedia, bool existingDvd, bool slotsFull, string bootOrder, string expectedOrder)
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
                new ObjectChange(typeof(VM), "template", new VM { uuid = "template-uuid", is_a_template = true, name_label = "Custom template",
                    HVM_boot_policy = "BIOS order", VBDs = existingDvd ? [new("new-cd")] : [] }),
                new ObjectChange(typeof(VM), "new-vm", new VM { uuid = "new-vm-uuid", HVM_boot_params = new() { ["order"] = bootOrder },
                    VBDs = existingDvd ? [new("new-cd")] : [] }),
                new ObjectChange(typeof(VBD), "new-cd", new VBD { uuid = "new-cd-uuid", VM = new("new-vm"), type = vbd_type.CD, userdevice = "3", empty = true })]);
            var action = new CreateVMAction(connection, connection.Resolve(new XenRef<VM>("template")), null!, "new VM", "",
                selectMedia ? InstallMethod.CD : InstallMethod.None, "", new VDI { opaque_ref = "iso" }, "", VmBootMode.Bios,
                null!, 2, 2, 1L << 30, 1L << 30, 1L << 30, [new DiskDescription()], null!, [], false, false, (_, _) => { }, (_, _) => { }, null!, false, 1, "")
                { Session = connection.Session, VM = connection.Resolve(new XenRef<VM>("new-vm")) };
            // Seed cancellation's borrowed session so the probe never logs in again.
            typeof(XenAdmin.Actions.CancellingAction).GetField("BorrowedSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(action, connection.Session);
            JObject? drive = null;
            server.ResultForRequest = request =>
            {
                var method = request["method"]!.Value<string>();
                if (method == "VM.get_allowed_VBD_devices") return slotsFull ? new JArray("4", "5") : new JArray("4", "0", "3");
                if (method == "Async.VBD.create") { drive = (JObject)request["params"]![1]!; return new JValue("create-task"); }
                if (method == "task.get_record") return new XenAPI.Task
                    { uuid = "task-uuid", status = task_status_type.success, result = "<value>new-cd</value>" }.ToJObject();
                if (method == "task.get_allowed_operations") return new JArray();
                if (method == "Async.VBD.insert") return new JValue("insert-task");
                return new JValue("ok");
            };
            var addCd = typeof(CreateVMAction).GetMethod("AddCdDrive", BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (!selectMedia) { addCd.Invoke(action, null); Assert.Empty(server.Requests); return; }
            typeof(CreateVMAction).GetMethod("SetVMBootParams", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(action, null);
            var boot = Assert.Single(server.Requests, r => r["method"]!.Value<string>() == "VM.set_HVM_boot_params");
            Assert.Equal(expectedOrder, boot["params"]![2]!["order"]!.Value<string>());
            if (slotsFull)
            {
                var failure = Assert.Throws<TargetInvocationException>(() => addCd.Invoke(action, null));
                Assert.Contains("No CD/DVD slot (0-3)", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
                Assert.DoesNotContain(server.Requests, r => r["method"]!.Value<string>() is "Async.VBD.create" or "Async.VBD.insert");
                return;
            }
            addCd.Invoke(action, null);
            if (existingDvd) Assert.Null(drive);
            else
            {
                Assert.NotNull(drive);
                Assert.Equal("RO", drive["mode"]!.Value<string>());
                Assert.True(drive["bootable"]!.Value<bool>());
                Assert.Equal("3", drive["userdevice"]!.Value<string>());
            }
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
