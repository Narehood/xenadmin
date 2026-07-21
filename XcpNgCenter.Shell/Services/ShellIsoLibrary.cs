using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Enumerates ISO library VDIs for the console ISO selector / attach dialog.
/// </summary>
public static class ShellIsoLibrary
{
    public static IEnumerable<IsoOption> Enumerate(VM vm)
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

    public static VDI? GetAttachedIso(VM vm)
    {
        var cdrom = vm.FindVMCDROM();
        if (cdrom == null)
            return null;
        var vdi = vm.Connection.Resolve(cdrom.VDI);
        if (vdi == null || vdi.IsToolsIso())
            return null;
        return vdi;
    }

    public static string FormatAttachedLabel(VM? vm)
    {
        if (vm == null)
            return "No VM selected";
        var vdi = GetAttachedIso(vm);
        return vdi == null ? "Empty — no ISO inserted" : Helpers.GetName(vdi);
    }
}
