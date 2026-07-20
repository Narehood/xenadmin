using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class VmEditViewModel : ViewModelBase
{
    private readonly VM _vm;
    private readonly Action _close;

    public VmEditViewModel(VM vm, Action close)
    {
        _vm = vm;
        _close = close;
        NameLabel = vm.name_label ?? string.Empty;
        VcpusText = Math.Max(1, vm.VCPUs_at_startup).ToString();
        MemoryMibText = Math.Max(1, vm.memory_dynamic_max / (1024 * 1024)).ToString();
    }

    [ObservableProperty]
    private string _nameLabel = string.Empty;

    [ObservableProperty]
    private string _vcpusText = "1";

    [ObservableProperty]
    private string _memoryMibText = "1024";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Apply()
    {
        if (string.IsNullOrWhiteSpace(NameLabel))
        {
            StatusMessage = "Enter a VM name.";
            return;
        }

        if (!long.TryParse(VcpusText.Trim(), out var vcpus) || vcpus < 1)
        {
            StatusMessage = "Enter a valid vCPU count.";
            return;
        }

        if (!long.TryParse(MemoryMibText.Trim(), out var mib) || mib < 1)
        {
            StatusMessage = "Enter memory in MiB.";
            return;
        }

        var bytes = mib * 1024L * 1024L;
        var name = NameLabel.Trim();

        if (!string.Equals(name, _vm.name_label, StringComparison.Ordinal))
        {
            ShellActionRunner.Run(new DelegatedAsyncAction(
                _vm.Connection,
                $"Rename {_vm.name_label}",
                "Renaming…",
                "Renamed.",
                session => VM.set_name_label(session, _vm.opaque_ref, name),
                "VM.set_name_label"));
        }

        if (vcpus != _vm.VCPUs_at_startup || vcpus != _vm.VCPUs_max)
        {
            var max = Math.Max(vcpus, _vm.VCPUs_max);
            ShellActionRunner.Run(new ChangeVCPUSettingsAction(_vm, max, vcpus));
        }

        if (bytes != _vm.memory_dynamic_max || bytes != _vm.memory_static_max)
        {
            var staticMin = Math.Min(_vm.memory_static_min, bytes);
            var dynamicMin = Math.Min(_vm.memory_dynamic_min, bytes);
            ShellActionRunner.Run(new ChangeMemorySettingsAction(
                _vm,
                $"Set memory on {name}",
                staticMin,
                dynamicMin,
                bytes,
                bytes,
                (_, _) => { },
                (_, _) => { },
                suppressHistory: false));
        }

        StatusMessage = "Changes queued — see Logs.";
        _close();
    }
}
