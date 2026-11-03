using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.Views;
using XenAdmin;
using XenAdmin.Actions;
using XenAdmin.Actions.VMActions;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.Tests;

[Collection("Infrastructure rendering")]
public sealed class NewVmWizardTests(InfrastructureRenderingFixture rendering)
{
    [Theory]
    [InlineData(VmBootMode.Bios, false, false)]
    [InlineData(VmBootMode.Uefi, true, true)]
    [InlineData(VmBootMode.SecureUefi, true, true)]
    public Task WizardSubmitsSelectedMediaFirmwareTpmAndTopology(VmBootMode mode, bool tpm, bool media)
        => rendering.Run(() =>
        {
            using var fixture = new WizardFixture();
            var model = fixture.Model;
            model.NextCommand.Execute(null);
            model.NextCommand.Execute(null);
            Assert.True(model.ShowInstallationStep);
            model.SelectedMedia = model.InstallationMedia.Single(o => o.Vdi?.opaque_ref == (media ? "iso" : null));
            model.SelectedBootMode = model.BootModes.Single(o => o.Mode == mode);
            model.EnableVtpm = tpm;
            model.NextCommand.Execute(null);
            model.VcpusText = "4";
            model.VcpusMaxText = "8";
            model.CoresPerSocketText = "4";
            Assert.Contains("2 sockets", model.CpuTopologyText);
            for (var i = 0; i < 3; i++) model.NextCommand.Execute(null);
            Assert.True(model.IsLastStep, model.StatusMessage);
            Assert.Contains(model.SelectedMedia.Label, model.SummaryText);
            Assert.Contains(model.SelectedBootMode.Label, model.SummaryText);
            model.NextCommand.Execute(null);
            var action = Assert.IsType<CreateVMAction>(fixture.Queued);
            Assert.True(fixture.Closed);
            Assert.Equal(media ? InstallMethod.CD : InstallMethod.None, Field<InstallMethod>(action, "_installMethod"));
            Assert.Equal(media ? "iso" : null, Field<VDI?>(action, "_cd")?.opaque_ref);
            Assert.Equal(mode, Field<VmBootMode>(action, "_bootMode"));
            Assert.Equal(tpm, Field<bool>(action, "_assignVtpm"));
            Assert.Equal(8, Field<long>(action, "_vcpusMax"));
            Assert.Equal(4, Field<long>(action, "_vcpusAtStartup"));
            Assert.Equal(4, Field<long>(action, "_coresPerSocket"));
            Assert.Empty(fixture.Server.Requests);
        });

    [Fact]
    public Task MediaFiltersHostAccessAndRevalidatesBeforeCreate()
        => rendering.Run(() =>
        {
            using var fixture = new WizardFixture();
            var model = fixture.Model;
            Assert.Contains(model.InstallationMedia, o => o.Vdi?.opaque_ref == "dvd");
            Assert.DoesNotContain(model.InstallationMedia, o => o.Vdi?.opaque_ref is "other-iso" or "snapshot" or "tools");
            model.SelectedMedia = model.InstallationMedia.Single(o => o.Vdi?.opaque_ref == "dvd");
            model.SelectedHost = model.Hosts.Single(h => h.opaque_ref == "other-host");
            Assert.Null(model.SelectedMedia?.Vdi);
            Assert.DoesNotContain(model.InstallationMedia, o => o.Vdi?.opaque_ref == "dvd");
            model.SelectedMedia = model.InstallationMedia.Single(o => o.Vdi?.opaque_ref == "iso");
            model.SelectedHost = model.Hosts.Single(h => h.opaque_ref == "synthetic-host");
            Assert.Equal("iso", model.SelectedMedia?.Vdi?.opaque_ref);
            for (var i = 0; i < 6; i++) model.NextCommand.Execute(null);
            Assert.True(model.IsLastStep);
            fixture.Connection.Resolve(new XenRef<PBD>("iso-pbd")).currently_attached = false;
            model.NextCommand.Execute(null);
            Assert.Null(fixture.Queued);
            Assert.False(fixture.Closed);
            Assert.Contains("no longer available", model.StatusMessage);
            Assert.Empty(fixture.Server.Requests);
        });

    [Theory]
    [InlineData("4", "3", "1")]
    [InlineData("4", "8", "3")]
    [InlineData("4", "8", "0")]
    [InlineData("4", "16", "16")]
    public Task InvalidCpuTopologyCannotAdvance(string startup, string maximum, string cores)
        => rendering.Run(() =>
        {
            using var fixture = new WizardFixture();
            var model = fixture.Model;
            for (var i = 0; i < 3; i++) model.NextCommand.Execute(null);
            Assert.True(model.ShowComputeStep);
            model.VcpusText = startup; model.VcpusMaxText = maximum; model.CoresPerSocketText = cores;
            model.NextCommand.Execute(null);
            Assert.True(model.ShowComputeStep);
            Assert.NotEmpty(model.StatusMessage);
            Assert.Null(fixture.Queued);
        });

    [Theory]
    [InlineData("true", "", VmBootMode.SecureUefi)]
    [InlineData("auto", "synthetic-certificates", VmBootMode.SecureUefi)]
    [InlineData("auto", "", VmBootMode.Uefi)]
    public Task FirmwareDefaultsAndInheritedTpmArePreserved(string secureBoot, string poolCertificates, VmBootMode expectedMode)
        => rendering.Run(() =>
        {
            using var fixture = new WizardFixture();
            var template = fixture.Model.SelectedTemplate!.Template;
            template.HVM_boot_params = new() { ["firmware"] = "uefi" };
            template.platform = new() { ["vtpm"] = "true", ["secureboot"] = secureBoot };
            fixture.Connection.Resolve(new XenRef<Pool>("synthetic-pool")).uefi_certificates = poolCertificates;
            template.last_boot_CPU_flags = new() { ["vendor"] = "synthetic" };
            fixture.Model.SelectedTemplate = null;
            fixture.Model.SelectedTemplate = fixture.Model.Templates[0];
            Assert.Equal(expectedMode, fixture.Model.SelectedBootMode?.Mode);
            Assert.DoesNotContain(fixture.Model.BootModes, o => o.Mode == VmBootMode.Bios);
            Assert.True(fixture.Model.EnableVtpm);
            Assert.False(fixture.Model.CanEditVtpm);
            for (var i = 0; i < 7; i++) fixture.Model.NextCommand.Execute(null);
            var action = Assert.IsType<CreateVMAction>(fixture.Queued);
            Assert.False(Field<bool>(action, "_assignVtpm"));
            Assert.Equal(expectedMode, Field<VmBootMode>(action, "_bootMode"));
            Assert.True(Field<bool>(action, "_startAfter"));
        });

    [Fact]
    public Task ChangedPoolCapabilitiesBlockCreationInsteadOfDroppingSecuritySettings()
        => rendering.Run(() =>
        {
            using var fixture = new WizardFixture();
            var model = fixture.Model;
            model.SelectedBootMode = model.BootModes.Single(o => o.Mode == VmBootMode.SecureUefi);
            model.EnableVtpm = true;
            for (var i = 0; i < 6; i++) model.NextCommand.Execute(null);
            fixture.Connection.Resolve(new XenRef<Host>("other-host")).license_params = new() { ["restrict_vtpm"] = "true" };
            model.NextCommand.Execute(null);
            Assert.Null(fixture.Queued);
            Assert.Contains("vTPM support", model.StatusMessage);
            model.SelectedBootMode = model.BootModes.Single(o => o.Mode == VmBootMode.Bios);
            Assert.False(model.EnableVtpm);
            Assert.False(model.CanEditVtpm);
        });

    [Fact]
    public Task PvTemplateDoesNotAdvertiseHvmFirmwareOrTpm()
        => rendering.Run(() =>
        {
            using var fixture = new WizardFixture();
            var model = fixture.Model;
            var option = model.SelectedTemplate!;
            option.Template.HVM_boot_policy = "";
            model.SelectedTemplate = null; model.SelectedTemplate = option;
            var window = new NewVmWizardWindow { DataContext = model };
            try
            {
                window.Show();
                model.NextCommand.Execute(null); model.NextCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.True(window.FindControl<ComboBox>("InstallationMediaPicker")!.IsEffectivelyVisible);
                Assert.False(window.FindControl<ComboBox>("BootModePicker")!.IsEffectivelyVisible);
                Assert.False(window.FindControl<CheckBox>("VtpmCheckBox")!.IsEffectivelyVisible);
                for (var i = 0; i < 4; i++) model.NextCommand.Execute(null);
                Assert.True(model.IsLastStep, model.StatusMessage);
                Assert.Contains("Boot: paravirtualized", model.SummaryText);
                Assert.DoesNotContain("Firmware:", model.SummaryText);
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task ProductionWizardBindingsExposeAndRetainTheNewChoices()
        => rendering.Run(() =>
        {
            using var fixture = new WizardFixture();
            var window = new NewVmWizardWindow { DataContext = fixture.Model };
            try
            {
                window.Show();
                fixture.Model.NextCommand.Execute(null);
                fixture.Model.NextCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                var media = window.FindControl<ComboBox>("InstallationMediaPicker")!;
                var boot = window.FindControl<ComboBox>("BootModePicker")!;
                var tpm = window.FindControl<CheckBox>("VtpmCheckBox")!;
                Assert.True(media.IsEffectivelyVisible);
                Assert.True(boot.IsEffectivelyVisible);
                media.SelectedItem = fixture.Model.InstallationMedia.Single(o => o.Vdi?.opaque_ref == "iso");
                boot.SelectedItem = fixture.Model.BootModes.Single(o => o.Mode == VmBootMode.SecureUefi);
                tpm.IsChecked = true;
                Assert.Equal("iso", fixture.Model.SelectedMedia?.Vdi?.opaque_ref);
                Assert.True(fixture.Model.EnableVtpm);
                fixture.Model.NextCommand.Execute(null);
                window.FindControl<TextBox>("MaximumCpusText")!.Text = "8";
                window.FindControl<TextBox>("CoresPerSocketText")!.Text = "4";
                Assert.Equal("8", fixture.Model.VcpusMaxText);
                Assert.Equal("4", fixture.Model.CoresPerSocketText);
                fixture.Model.BackCommand.Execute(null);
                Assert.Equal("iso", fixture.Model.SelectedMedia?.Vdi?.opaque_ref);
                Assert.Equal(VmBootMode.SecureUefi, fixture.Model.SelectedBootMode?.Mode);
                Assert.True(fixture.Model.EnableVtpm);
            }
            finally { window.Close(); }
        });

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private sealed class WizardFixture : IDisposable
    {
        private readonly IXenAdminConfigProvider _previous = XenAdminConfigManager.Provider;
        private readonly string _path = Path.Combine(Path.GetTempPath(), "new-vm-" + Guid.NewGuid().ToString("N"));
        private readonly ShellAppSettings _settings;
        // Keep the loopback accept loop off the headless UI synchronization context:
        // production actions perform synchronous RPCs on the test's calling thread.
        public JsonRpcTransportTests.RpcServer Server { get; } = Task.Run(() => new JsonRpcTransportTests.RpcServer()).GetAwaiter().GetResult();
        public XenConnection Connection { get; }
        public NewVmWizardViewModel Model { get; }
        public AsyncAction? Queued { get; private set; }
        public bool Closed { get; private set; }

        public WizardFixture()
        {
            _settings = new ShellAppSettings(_path);
            XenAdminConfigManager.Provider = new ShellConfigProvider(_settings);
            Connection = HeartbeatLifecycleTests.Connection(Server);
            var changes = new List<ObjectChange>();
            void Add<T>(string reference, T value) where T : XenObject<T> => changes.Add(new(typeof(T), reference, value));
            foreach (var host in new[] { "synthetic-host", "other-host" })
            {
                Add(host, new Host { uuid = host + "-uuid", name_label = host, enabled = true, metrics = new(host + "-metrics"),
                    software_version = new() { ["platform_version"] = "3.6.0", ["xapi"] = "23.11.0", ["product_version"] = "8.3.0" },
                    license_params = new() { ["restrict_vtpm"] = "false" }, cpu_info = new() { ["socket_count"] = "2", ["cpu_count"] = "16" } });
                Add(host + "-metrics", new Host_metrics { live = true });
            }
            Add("template", new VM { uuid = "template-uuid", name_label = "Synthetic OS", is_a_template = true,
                HVM_boot_policy = "BIOS order", HVM_boot_params = new() { ["order"] = "dc" },
                other_config = new() { ["default_template"] = "true" }, VCPUs_at_startup = 2, VCPUs_max = 2,
                memory_dynamic_min = 1L << 30, memory_dynamic_max = 2L << 30, memory_static_max = 2L << 30,
                recommendations = "<restrictions><restriction field='supports-uefi' value='yes'/><restriction field='supports-secure-boot' value='yes'/></restrictions>" });
            Add("sm", new SM { type = "ext", capabilities = ["VDI_CREATE"] });
            Add("disk-sr", new SR { uuid = "disk-sr-uuid", name_label = "VM disks", type = "ext", content_type = "user",
                PBDs = [new("disk-pbd")], physical_size = 1L << 40 });
            Add("disk-pbd", new PBD { SR = new("disk-sr"), host = new("synthetic-host"), currently_attached = true });
            Add("network", new XenAPI.Network { uuid = "network-uuid", name_label = "Guest network" });
            void Media(string sr, string vdi, string host, string type = "iso", bool snapshot = false, bool tools = false)
            {
                Add(sr, new SR { uuid = sr + "-uuid", name_label = sr, content_type = "iso", type = type,
                    VDIs = [new(vdi)], PBDs = [new(sr + "-pbd")], is_tools_sr = tools,
                    sm_config = type == "udev" ? new() { ["type"] = "cd" } : new() });
                Add(sr + "-pbd", new PBD { SR = new(sr), host = new(host), currently_attached = true });
                Add(vdi, new VDI { uuid = vdi + "-uuid", name_label = vdi + ".iso", SR = new(sr), is_a_snapshot = snapshot });
            }
            Media("iso", "iso", "synthetic-host");
            changes.Add(new(typeof(PBD), "iso-other-pbd", new PBD { SR = new("iso"), host = new("other-host"), currently_attached = true }));
            Media("dvd", "dvd", "synthetic-host", "udev");
            Media("other", "other-iso", "other-host");
            Media("snapshot", "snapshot", "synthetic-host", snapshot: true);
            Media("tools", "tools", "synthetic-host", tools: true);
            Connection.Cache.UpdateFrom(Connection, changes);
            Connection.Resolve(new XenRef<SR>("iso")).PBDs.Add(new("iso-other-pbd"));
            Model = new NewVmWizardViewModel(Connection, () => Closed = true, runAction: action => Queued = action);
        }

        public void Dispose()
        {
            Connection.Dispose(); Server.Dispose(); _settings.Dispose();
            XenAdminConfigManager.Provider = _previous;
            if (File.Exists(_path)) File.Delete(_path);
        }
    }
}
