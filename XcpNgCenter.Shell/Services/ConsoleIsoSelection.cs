using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

/// <summary>Keeps a pending ISO choice across inventory replacements for the same VM.</summary>
internal sealed class ConsoleIsoSelection
{
    private VM? _vm;

    public IsoOption? Refresh(VM? vm, IsoOption? pending, IReadOnlyList<IsoOption> available, VDI? attached)
    {
        var sameVm = vm != null && _vm != null
                     && ReferenceEquals(vm.Connection, _vm.Connection)
                     && vm.opaque_ref == _vm.opaque_ref;
        _vm = vm;
        if (vm == null)
            return null;
        return (sameVm && pending != null
                   ? available.FirstOrDefault(o => o.Vdi.opaque_ref == pending.Vdi.opaque_ref)
                   : null)
               ?? available.FirstOrDefault(o => o.Vdi.opaque_ref == attached?.opaque_ref);
    }
}
