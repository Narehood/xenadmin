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
public sealed class NewVmCreationHardwareTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RequestedTpmCannotBeSilentlySkipped(bool requestTpm, bool allowTpm)
    {
        var previous = XenAdminConfigManager.Provider;
        var path = Path.Combine(Path.GetTempPath(), "new-vm-tpm-" + Guid.NewGuid().ToString("N"));
        using var settings = new ShellAppSettings(path);
        XenAdminConfigManager.Provider = new ShellConfigProvider(settings);
        using var server = new JsonRpcTransportTests.RpcServer();
        using var connection = HeartbeatLifecycleTests.Connection(server);
        using var transport = connection.Session.JsonRpcClient;
        try
        {
            connection.Resolve(new XenRef<Host>("synthetic-host")).software_version = new() { ["xapi"] = "23.11.0" };
            connection.Cache.UpdateFrom(connection, [
                new ObjectChange(typeof(VM), "template", new VM { uuid = "template-uuid", is_a_template = true, name_label = "template" }),
                new ObjectChange(typeof(VM), "new-vm", new VM { uuid = "vm-uuid", name_label = "clone",
                    allowed_operations = allowTpm ? [vm_operations.create_vtpm] : [] }),
                new ObjectChange(typeof(VTPM), "new-tpm", new VTPM { uuid = "tpm-uuid", VM = new("new-vm") })]);
            var action = new CreateVMAction(connection, connection.Resolve(new XenRef<VM>("template")), null!, "new VM", "",
                InstallMethod.None, "", null!, "", VmBootMode.Uefi, null!, 2, 2, 1L << 30, 1L << 30, 1L << 30,
                [], null!, [], false, requestTpm, (_, _) => { }, (_, _) => { }, null!, false, 1, "")
                { Session = connection.Session, VM = connection.Resolve(new XenRef<VM>("new-vm")) };
            server.Result = new JValue("new-tpm");
            var assign = typeof(CreateVMAction).GetMethod("AssignVtpm", BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (requestTpm && !allowTpm)
            {
                var failure = Assert.Throws<TargetInvocationException>(() => assign.Invoke(action, null));
                Assert.Equal(Messages.VTPM_OPERATION_DISALLOWED_ADD, Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
                Assert.Empty(server.Requests);
            }
            else
            {
                assign.Invoke(action, null);
                if (requestTpm) Assert.Equal("VTPM.create", Assert.Single(server.Requests)["method"]!.Value<string>());
                else Assert.Empty(server.Requests);
            }
        }
        finally
        {
            XenAdminConfigManager.Provider = previous;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(VmBootMode.Bios, 0)]
    [InlineData(VmBootMode.Uefi, 0)]
    [InlineData(VmBootMode.SecureUefi, 0)]
    [InlineData(VmBootMode.Bios, 4)]
    [InlineData(VmBootMode.Uefi, 4)]
    [InlineData(VmBootMode.SecureUefi, 4)]
    public void CpuAndBootWritesPreserveRequestedTopologyWithoutCacheEvents(VmBootMode mode, int originalCores)
    {
        var previous = XenAdminConfigManager.Provider;
        var path = Path.Combine(Path.GetTempPath(), "new-vm-hardware-" + Guid.NewGuid().ToString("N"));
        using var settings = new ShellAppSettings(path);
        XenAdminConfigManager.Provider = new ShellConfigProvider(settings);
        using var server = new JsonRpcTransportTests.RpcServer();
        using var connection = HeartbeatLifecycleTests.Connection(server);
        using var transport = connection.Session.JsonRpcClient;
        try
        {
            var originalPlatform = new Dictionary<string, string> { ["nx"] = "true" };
            if (originalCores > 0) originalPlatform["cores-per-socket"] = originalCores.ToString();
            connection.Cache.UpdateFrom(connection, [
                new ObjectChange(typeof(VM), "template", new VM { uuid = "template-uuid", is_a_template = true,
                    HVM_boot_policy = "BIOS order", name_label = "template", memory_dynamic_min = 1L << 30,
                    memory_dynamic_max = 1L << 30, memory_static_max = 1L << 30 }),
                new ObjectChange(typeof(VM), "new-vm", new VM { uuid = "vm-uuid", name_label = "clone",
                    VCPUs_max = 4, VCPUs_at_startup = 4, power_state = vm_power_state.Halted,
                    platform = originalPlatform, HVM_boot_params = new() { ["order"] = "dc" } })]);
            var action = new CreateVMAction(connection, connection.Resolve(new XenRef<VM>("template")), null!, "new VM", "",
                InstallMethod.None, "", null!, "", mode, null!, 6, 6, 1L << 30, 1L << 30, 1L << 30,
                [new DiskDescription()], null!, [], false, false, (_, _) => { }, (_, _) => { }, null!, false, 2, "")
                { Session = connection.Session, VM = connection.Resolve(new XenRef<VM>("new-vm")) };
            var serverCores = Math.Max(1, originalCores);
            server.ResultForRequest = request =>
            {
                var method = request["method"]!.Value<string>();
                if (method == "VM.set_platform") serverCores = request["params"]![2]!["cores-per-socket"]?.Value<int>() ?? 1;
                if (method == "VM.set_VCPUs_max" && request["params"]![2]!.Value<int>() % serverCores != 0)
                    server.RawBody = new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"],
                        ["error"] = new JObject { ["code"] = 1, ["message"] = "INVALID_VALUE",
                            ["data"] = new JArray("VCPUs_max", "must be a multiple of cores-per-socket") } }.ToString();
                return JValue.CreateNull();
            };
            // Run the same two consecutive production boundaries as CreateVMAction.Run.
            // The server deliberately delivers no cache event after either platform write.
            foreach (var method in new[] { "SetVMParams", "SetVMBootParams" })
                typeof(CreateVMAction).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(action, null);
            var platforms = server.Requests.Where(r => r["method"]!.Value<string>() == "VM.set_platform").ToArray();
            Assert.Equal("2", platforms[^1]["params"]![2]!["cores-per-socket"]?.Value<string>());
            Assert.Equal(mode == VmBootMode.SecureUefi ? "true" : "false", platforms[^1]["params"]![2]!["secureboot"]!.Value<string>());
            Assert.Equal("true", platforms[^1]["params"]![2]!["nx"]!.Value<string>());
            var firmware = Assert.Single(server.Requests, r => r["method"]!.Value<string>() == "VM.set_HVM_boot_params");
            Assert.Equal(mode == VmBootMode.Bios ? "bios" : "uefi", firmware["params"]![2]!["firmware"]!.Value<string>());
        }
        finally
        {
            XenAdminConfigManager.Provider = previous;
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
