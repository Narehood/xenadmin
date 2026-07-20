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
        var actions = new List<AsyncAction>();

        if (!string.Equals(name, _vm.name_label, StringComparison.Ordinal))
        {
            actions.Add(new DelegatedAsyncAction(
                _vm.Connection,
                $"Rename {_vm.name_label}",
                "Renaming…",
                "Renamed.",
                session => VM.set_name_label(session, _vm.opaque_ref, name),
                true,
                "VM.set_name_label"));
        }

        // Only compare against the field the dialog edits (startup count), not VCPUs_max.
        if (vcpus != _vm.VCPUs_at_startup)
        {
            var max = Math.Max(vcpus, _vm.VCPUs_max);
            actions.Add(new ChangeVCPUSettingsAction(_vm, max, vcpus));
        }

        // Only compare against dynamic_max (what we seed from); preserve static_max ceiling.
        if (bytes != _vm.memory_dynamic_max)
        {
            var staticMax = Math.Max(_vm.memory_static_max, bytes);
            var staticMin = Math.Min(_vm.memory_static_min, bytes);
            var dynamicMin = Math.Min(_vm.memory_dynamic_min, bytes);
            actions.Add(new ChangeMemorySettingsAction(
                _vm,
                $"Set memory on {name}",
                staticMin,
                dynamicMin,
                bytes,
                staticMax,
                (_, _) => { },
                (_, _) => { },
                suppressHistory: true));
        }

        if (actions.Count == 0)
        {
            StatusMessage = "No changes to apply.";
            _close();
            return;
        }

        AsyncAction toRun = actions.Count == 1
            ? actions[0]
            : new MultipleAction(
                _vm.Connection,
                $"Update {name}",
                "Updating…",
                $"Updated {name}.",
                actions,
                stopOnFirstException: true);

        ShellActionRunner.Run(toRun);
        StatusMessage = "Changes queued — see Logs.";
        _close();
    }
}
