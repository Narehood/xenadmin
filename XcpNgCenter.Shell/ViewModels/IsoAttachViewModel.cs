using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions;
using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public sealed record IsoOption(string Name, string Subtitle, VDI Vdi);

public partial class IsoAttachViewModel : ViewModelBase
{
    private readonly VM _vm;
    private readonly Action _close;

    public IsoAttachViewModel(VM vm, Action close)
    {
        _vm = vm;
        _close = close;
        foreach (var iso in EnumerateIsos(vm))
            Isos.Add(iso);
        HasIsos = Isos.Count > 0;
        StatusMessage = HasIsos
            ? $"Select an ISO for {_vm.name_label}."
            : "No ISO VDIs found on connected ISO libraries.";
    }

    public ObservableCollection<IsoOption> Isos { get; } = new();

    [ObservableProperty]
    private IsoOption? _selectedIso;

    [ObservableProperty]
    private bool _hasIsos;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Attach()
    {
        if (SelectedIso == null)
        {
            StatusMessage = "Select an ISO.";
            return;
        }

        EnsureCdAndChange(SelectedIso.Vdi);
        _close();
    }

    [RelayCommand]
    private void Eject()
    {
        EnsureCdAndChange(vdi: null);
        _close();
    }

    private void EnsureCdAndChange(VDI? vdi)
    {
        var cdrom = _vm.FindVMCDROM();
        if (cdrom == null)
        {
            var create = new CreateCdDriveAction(_vm);
            create.Completed += a =>
            {
                if (!a.Succeeded)
                    return;
                var refreshed = _vm.Connection.Resolve(new XenRef<VM>(_vm.opaque_ref)) ?? _vm;
                var drive = refreshed.FindVMCDROM();
                if (drive != null)
                    ShellActionRunner.Run(new ChangeVMISOAction(refreshed.Connection, refreshed, vdi, drive));
            };
            ShellActionRunner.Run(create);
            return;
        }

        ShellActionRunner.Run(new ChangeVMISOAction(_vm.Connection, _vm, vdi, cdrom));
    }

    private static IEnumerable<IsoOption> EnumerateIsos(VM vm)
    {
        var conn = vm.Connection;
        if (conn == null)
            yield break;

        foreach (var sr in conn.Cache.SRs
                     .Where(sr => sr != null && sr.content_type == "iso" && !sr.IsToolsSR())
                     .OrderBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase))
        {
            foreach (var vdi in conn.ResolveAll(sr.VDIs)
                         .Where(v => v != null && !v.is_a_snapshot)
                         .OrderBy(v => Helpers.GetName(v), StringComparer.OrdinalIgnoreCase))
            {
                yield return new IsoOption(
                    Helpers.GetName(vdi),
                    Helpers.GetName(sr),
                    vdi);
            }
        }
    }
}
