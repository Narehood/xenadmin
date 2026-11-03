using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.Views;
using Console = System.Console;

sealed partial class ProbeApp
{
    void CheckNewVm(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        DispatcherTimer.RunOnce(async () =>
        {
            var previous = XenAdminConfigManager.Provider;
            using var connection = new XenConnection();
            try
            {
                XenAdminConfigManager.Provider = new ShellConfigProvider(settings!);
                connection.Cache.UpdateFrom(connection, [
                    new ObjectChange(typeof(Host), "host", new Host { name_label = "Synthetic host", uuid = "host-uuid",
                        software_version = new() { ["platform_version"] = "3.6.0", ["xapi"] = "23.11.0" },
                        license_params = new() { ["restrict_vtpm"] = "false" }, cpu_info = new() { ["socket_count"] = "2", ["cpu_count"] = "16" } }),
                    new ObjectChange(typeof(Pool), "pool", new Pool { master = new("host") }),
                    new ObjectChange(typeof(VM), "template", new VM { uuid = "template-uuid", name_label = "Synthetic Windows installer", is_a_template = true,
                        HVM_boot_policy = "BIOS order", VCPUs_at_startup = 2, VCPUs_max = 2, memory_dynamic_max = 4L << 30,
                        other_config = new() { ["default_template"] = "true" },
                        recommendations = "<restrictions><restriction field='supports-uefi' value='yes'/><restriction field='supports-secure-boot' value='yes'/></restrictions>" }),
                    new ObjectChange(typeof(SR), "iso-sr", new SR { uuid = "iso-sr-uuid", name_label = "Install media", content_type = "iso",
                        VDIs = [new("iso")], PBDs = [new("pbd")] }),
                    new ObjectChange(typeof(PBD), "pbd", new PBD { host = new("host"), SR = new("iso-sr"), currently_attached = true }),
                    new ObjectChange(typeof(VDI), "iso", new VDI { uuid = "iso-uuid", name_label = "Windows-install.iso", SR = new("iso-sr") })]);
                var model = new NewVmWizardViewModel(connection, () => { }, runAction: _ => throw new InvalidOperationException("Probe must not queue actions"));
                var host = connection.Resolve(new XenRef<Host>("host"));
                model.Hosts.Add(host); model.SelectedHost = host;
                var window = new NewVmWizardWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-10000, -10000) };
                lifetime.MainWindow = window;
                window.Show();
                model.NextCommand.Execute(null); model.NextCommand.Execute(null);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                var media = window.FindControl<ComboBox>("InstallationMediaPicker")!;
                var firmware = window.FindControl<ComboBox>("BootModePicker")!;
                var tpm = window.FindControl<CheckBox>("VtpmCheckBox")!;
                Require(media.IsEffectivelyVisible && firmware.IsEffectivelyVisible && tpm.IsEffectivelyVisible,
                    "Production wizard displays ISO/DVD, firmware and TPM controls on the new setup step");
                media.SelectedItem = model.InstallationMedia.Single(o => o.Vdi != null);
                firmware.SelectedItem = model.BootModes.Single(o => o.Mode == VmBootMode.SecureUefi);
                tpm.IsChecked = true;
                Require(model.SelectedMedia?.Vdi?.opaque_ref == "iso" && model.SelectedBootMode?.Mode == VmBootMode.SecureUefi && model.EnableVtpm,
                    "Actual selectors submit ISO, Secure Boot and TPM choices to the draft");
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                window.UpdateLayout();
                foreach (var scale in new[] { 1d, 1.5d, 2d }) Render(window, "media-firmware-tpm", scale);
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                window.UpdateLayout(); Render(window, "media-minimum", 1);
                model.NextCommand.Execute(null);
                window.FindControl<TextBox>("StartupCpusText")!.Text = "4";
                window.FindControl<TextBox>("MaximumCpusText")!.Text = "8";
                window.FindControl<TextBox>("CoresPerSocketText")!.Text = "4";
                Require(model.VcpusText == "4" && model.VcpusMaxText == "8" && model.CpuTopologyText.Contains("2 sockets"),
                    "Actual CPU inputs update startup, maximum and sockets/cores topology");
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                Render(window, "cpu-topology-minimum", 1);
                model.BackCommand.Execute(null);
                Require(model.SelectedMedia?.Vdi?.opaque_ref == "iso" && model.EnableVtpm && model.SelectedBootMode?.Mode == VmBootMode.SecureUefi,
                    "Back navigation retains media and security settings");
                window.Close();
                File.WriteAllLines(Path.Combine(Evidence, "results.log"), checks.Prepend(
                    "PASS: production Windows New VM wizard with synthetic inventory; no profiles or pool RPCs."));
                Console.WriteLine($"Passed {checks.Count} New VM UI checks. Evidence: {Evidence}");
                theme?.Dispose(); settings?.Dispose(); lifetime.Shutdown(0);
            }
            catch (Exception error) { Fail(lifetime, error); }
            finally { XenAdminConfigManager.Provider = previous; }
        }, TimeSpan.FromMilliseconds(100));
    }
}
