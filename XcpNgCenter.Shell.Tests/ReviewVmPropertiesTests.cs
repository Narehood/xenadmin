using System.Reflection;
using Newtonsoft.Json.Linq;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using XenAdmin;
using XenAdmin.Actions;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

[Collection("Action session ownership")]
public sealed class ReviewVmPropertiesTests
{
    /// <summary>Runs a combined name/memory Apply through the real batch, rejecting stale memory before any property RPC.</summary>
    /// <param name="stale">Whether live memory changes after the batch is queued.</param>
    /// <param name="cacheUpdated">Whether the fixture delivers the memory change before the general save runs.</param>
    /// <param name="editMemory">Whether memory is edited alongside the name; false checks preservation of an external memory change.</param>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    public void CombinedPropertiesChecksMemoryBeforeSavingOtherFields(bool stale, bool cacheUpdated, bool editMemory)
    {
        var previous = XenAdminConfigManager.Provider;
        var path = Path.Combine(Path.GetTempPath(), "review-properties-" + Guid.NewGuid().ToString("N"));
        using var settings = new ShellAppSettings(path);
        XenAdminConfigManager.Provider = new ShellConfigProvider(settings);
        using var server = new JsonRpcTransportTests.RpcServer();
        using var connection = HeartbeatLifecycleTests.Connection(server);
        // Production action sessions bind the transport to the connection's cache.
        var session = new Session(connection.Session, connection);
        using var transport = session.JsonRpcClient;
        MultipleAction? queued = null;
        try
        {
            connection.Cache.UpdateFrom(connection, [
                new ObjectChange(typeof(VM), "vm", new VM { uuid = "vm-uuid", name_label = "VM", power_state = vm_power_state.Running,
                    VCPUs_at_startup = 2, VCPUs_max = 4, memory_static_min = 256L << 20, memory_dynamic_min = 2L << 30,
                    memory_dynamic_max = 8L << 30, memory_static_max = 16L << 30, PV_bootloader = "pygrub", resident_on = new("synthetic-host") })]);
            var vm = connection.Resolve(new XenRef<VM>("vm"));
            server.ResultForRequest = request =>
            {
                // Deliver an immediate event to expose any general-save memory rollback.
                if (cacheUpdated && request["method"]!.ToString() == "VM.set_memory_dynamic_range")
                {
                    vm.memory_dynamic_min = request["params"]![2]!.ToObject<long>();
                    vm.memory_dynamic_max = request["params"]![3]!.ToObject<long>();
                }
                return new JValue("ok");
            };
            var editor = new VmPropertiesViewModel(vm, () => { }, null, action => queued = (MultipleAction)action)
            {
                NameLabel = "renamed"
            };
            if (editMemory) editor.MemoryMibText = "9216";
            if (stale && !editMemory) vm.memory_dynamic_max += 1L << 20;
            editor.ApplyCommand.Execute(null);
            Assert.NotNull(queued);
            var save = Assert.Single(queued.SubActions.OfType<SaveChangesAction>());
            if (stale && editMemory)
            {
                vm.memory_dynamic_max += 1L << 20;
                var error = Assert.Throws<InvalidOperationException>(() => queued.RunSync(session));
                Assert.Contains("Reopen Properties", error.Message);
                Assert.Empty(server.Requests);
                Assert.False(save.StartedRunning);
                Assert.Equal("VM", vm.name_label);
            }
            else
            {
                queued.RunSync(session);
                var methods = server.Requests.Select(request => request["method"]!.ToString()).ToArray();
                if (editMemory)
                {
                    Assert.Equal("VM.set_memory_dynamic_range", methods[0]);
                    Assert.Single(methods, method => method.StartsWith("VM.set_memory_", StringComparison.Ordinal));
                }
                else Assert.DoesNotContain(methods, method => method.StartsWith("VM.set_memory_", StringComparison.Ordinal));
                Assert.Contains("VM.set_name_label", methods);
                Assert.True(save.Succeeded, save.Exception?.Message);
            }
        }
        finally
        {
            queued?.Dispose();
            XenAdminConfigManager.Provider = previous;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>Checks untouched drafts, live and halted memory edits, stale-state rejection and all-or-nothing validation before action construction.</summary>
    /// <param name="edit">The requested draft edit or null for untouched Apply.</param>
    /// <param name="memoryExpected">Whether a memory action should be queued.</param>
    /// <param name="halted">Whether the guest is halted when the draft opens.</param>
    [Theory]
    [InlineData(null, false, false)]
    [InlineData("rename", false, false)]
    [InlineData("memory", true, false)]
    [InlineData("too large", false, false)]
    [InlineData("too large", true, true)]
    [InlineData("memory", true, true)]
    public void PropertiesOnlyQueuesExplicitLiveSafeHardwareEdits(string? edit, bool memoryExpected, bool halted)
    {
        var previous = XenAdminConfigManager.Provider;
        var path = Path.Combine(Path.GetTempPath(), "review-properties-" + Guid.NewGuid().ToString("N"));
        using var settings = new ShellAppSettings(path);
        XenAdminConfigManager.Provider = new ShellConfigProvider(settings);
        using var connection = new XenConnection();
        try
        {
            connection.Cache.UpdateFrom(connection, [
                new ObjectChange(typeof(Host), "host", new Host { uuid = "host-uuid", software_version = new() { ["product_version"] = "8.3.0" } }),
                new ObjectChange(typeof(Pool), "pool", new Pool { master = new("host") }),
                new ObjectChange(typeof(VM), "vm", new VM { uuid = "vm-uuid", name_label = "VM", power_state = halted ? vm_power_state.Halted : vm_power_state.Running,
                    VCPUs_at_startup = 2, VCPUs_max = 4, memory_static_min = 256L << 20, memory_dynamic_min = 2L << 30,
                    memory_dynamic_max = 8L << 30, memory_static_max = 16L << 30, PV_bootloader = "pygrub", resident_on = new("host") })]);
            MultipleAction? queued = null;
            var vm = connection.Resolve(new XenRef<VM>("vm"));
            var editor = new VmPropertiesViewModel(vm, () => { }, null, action => queued = (MultipleAction)action);
            if (edit == "rename") editor.NameLabel = "renamed";
            if (edit == "memory") editor.MemoryMibText = "9216";
            if (edit == "too large") { editor.MemoryMibText = "32768"; editor.NameLabel = "pending name"; }
            int constructed = 0;
            // Rejected drafts must not leave constructed actions in global history.
            void Count(ActionBase _) => constructed++;
            ActionBase.NewAction += Count;
            try { editor.ApplyCommand.Execute(null); }
            finally { ActionBase.NewAction -= Count; }
            Assert.Empty(queued?.SubActions.OfType<ChangeVCPUSettingsAction>() ?? []);
            if (memoryExpected)
            {
                var memory = Assert.Single(queued!.SubActions.OfType<ChangeMemorySettingsAction>());
                Assert.False(memory.RequiresReboot);
                Assert.DoesNotContain(memory.GetApiMethodsToRoleCheck, m => m.Method.Contains("shutdown") || m.Method.Contains("start"));
                Assert.Equal((edit == "too large" ? 32L : 16L) << 30, typeof(ChangeMemorySettingsAction).GetField("_staticMax", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory));
                vm.memory_dynamic_max += 1L << 20;
                var error = Assert.Throws<TargetInvocationException>(() => typeof(ChangeMemorySettingsAction)
                    .GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(memory, null));
                Assert.IsType<InvalidOperationException>(error.InnerException);
            }
            else Assert.Empty(queued?.SubActions.OfType<ChangeMemorySettingsAction>() ?? []);
            if (edit == "too large" && !halted)
            {
                Assert.Null(queued);
                Assert.Contains("Shut down", editor.StatusMessage);
                Assert.Contains("No changes have been applied", editor.StatusMessage);
                Assert.Equal(0, constructed);
                Assert.Equal("pending name", editor.NameLabel);
                Assert.Equal("VM", vm.name_label);
            }
            if (edit == null) Assert.Null(queued);
            queued?.Dispose();
        }
        finally { XenAdminConfigManager.Provider = previous; if (File.Exists(path)) File.Delete(path); }
    }
}
