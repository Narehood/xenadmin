using System.Reflection;
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
            if (edit == "too large") editor.MemoryMibText = "32768";
            editor.ApplyCommand.Execute(null);
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
            }
            if (edit == null) Assert.Null(queued);
            queued?.Dispose();
        }
        finally { XenAdminConfigManager.Provider = previous; if (File.Exists(path)) File.Delete(path); }
    }
}
