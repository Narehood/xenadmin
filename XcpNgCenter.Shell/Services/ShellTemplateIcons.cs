using Avalonia.Media.Imaging;
using Avalonia.Platform;
using XenAPI;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// OS / template icons for the New VM wizard (pulled from WinForms XenAdmin Images).
/// </summary>
public static class ShellTemplateIcons
{
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    public static Bitmap ForTemplate(VM template) => ForType(template.TemplateType());

    public static Bitmap ForType(VM.VmTemplateType type) => type switch
    {
        VM.VmTemplateType.Custom => Load("000_UserTemplate_h32bit_16.png"),
        VM.VmTemplateType.Windows or VM.VmTemplateType.WindowsServer or VM.VmTemplateType.LegacyWindows
            => Load("windows_h32bit_16.png"),
        VM.VmTemplateType.Centos => Load("centos_16x.png"),
        VM.VmTemplateType.Debian => Load("debian_16x.png"),
        VM.VmTemplateType.Gooroom => Load("gooroom_16x.png"),
        VM.VmTemplateType.Rocky => Load("rocky_16x.png"),
        VM.VmTemplateType.Linx => Load("linx_16x.png"),
        VM.VmTemplateType.Oracle => Load("oracle_16x.png"),
        VM.VmTemplateType.RedHat => Load("redhat_16x.png"),
        VM.VmTemplateType.SciLinux => Load("scilinux_16x.png"),
        VM.VmTemplateType.Suse => Load("suse_16x.png"),
        VM.VmTemplateType.Ubuntu => Load("ubuntu_16x.png"),
        VM.VmTemplateType.YinheKylin => Load("yinhekylin_16x.png"),
        VM.VmTemplateType.NeoKylin => Load("neokylin_16x.png"),
        VM.VmTemplateType.Asianux => Load("asianux_16x.png"),
        VM.VmTemplateType.Turbo => Load("turbo_16x.png"),
        VM.VmTemplateType.Citrix => Load("Logo.png"),
        VM.VmTemplateType.CoreOS => Load("coreos-globe-icon.png"),
        VM.VmTemplateType.Snapshot or VM.VmTemplateType.SnapshotFromVmpp
            => Load("000_VMSession_h32bit_16.png"),
        _ => Load("000_VMTemplate_h32bit_16.png")
    };

    public static string TypeLabel(VM.VmTemplateType type) => type switch
    {
        VM.VmTemplateType.Custom => "Custom",
        VM.VmTemplateType.Windows or VM.VmTemplateType.WindowsServer or VM.VmTemplateType.LegacyWindows
            => "Windows",
        VM.VmTemplateType.Centos => "CentOS",
        VM.VmTemplateType.Debian => "Debian",
        VM.VmTemplateType.Gooroom => "Gooroom",
        VM.VmTemplateType.Rocky => "Rocky",
        VM.VmTemplateType.Linx => "Linx",
        VM.VmTemplateType.Oracle => "Oracle",
        VM.VmTemplateType.RedHat => "Red Hat",
        VM.VmTemplateType.SciLinux => "Scientific Linux",
        VM.VmTemplateType.Suse => "SUSE",
        VM.VmTemplateType.Ubuntu => "Ubuntu",
        VM.VmTemplateType.YinheKylin => "Yinhe Kylin",
        VM.VmTemplateType.NeoKylin => "NeoKylin",
        VM.VmTemplateType.Asianux => "Asianux",
        VM.VmTemplateType.Turbo => "Turbo",
        VM.VmTemplateType.Citrix => "XCP-ng",
        VM.VmTemplateType.CoreOS => "CoreOS",
        VM.VmTemplateType.Snapshot or VM.VmTemplateType.SnapshotFromVmpp => "Snapshots",
        VM.VmTemplateType.Solaris or VM.VmTemplateType.Misc => "Other",
        _ => "Template"
    };

    private static Bitmap Load(string fileName)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(fileName, out var cached))
                return cached;

            var uri = new Uri($"avares://XcpNgCenter.Shell/Assets/Templates/{fileName}");
            using var stream = AssetLoader.Open(uri);
            var bmp = new Bitmap(stream);
            Cache[fileName] = bmp;
            return bmp;
        }
    }
}
