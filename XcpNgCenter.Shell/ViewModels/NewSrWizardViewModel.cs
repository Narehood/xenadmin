using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public sealed record SrTypeOption(string Id, string Label);

public sealed class IscsiIqnOption
{
    public IscsiIqnOption(IScsiIqnInfo info)
    {
        Info = info;
        Label = string.IsNullOrWhiteSpace(info.TargetIQN)
            ? "(unknown IQN)"
            : info.IpAddress == null
                ? info.TargetIQN
                : $"{info.TargetIQN}  ({info.IpAddress}:{info.Port})";
    }

    public IScsiIqnInfo Info { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

public sealed class IscsiLunOption
{
    public IscsiLunOption(ISCSIInfo info)
    {
        Info = info;
        var size = info.Size > 0 ? $"{info.Size / (1024.0 * 1024 * 1024):0.##} GiB" : "?";
        var lun = info.LunID >= 0 ? $"LUN {info.LunID}" : "LUN";
        var vendor = string.IsNullOrWhiteSpace(info.Vendor) ? "" : $" · {info.Vendor}";
        Label = $"{lun} · {info.ScsiID} · {size}{vendor}";
    }

    public ISCSIInfo Info { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

public partial class NewSrWizardViewModel : ViewModelBase
{
    private readonly IXenConnection _connection;
    private readonly Host _host;
    private readonly Action _close;
    private bool _probing;

    public NewSrWizardViewModel(IXenConnection connection, Host host, Action close)
    {
        _connection = connection;
        _host = host;
        _close = close;
        Types.Add(new SrTypeOption("iso-nfs", "ISO library (NFS)"));
        Types.Add(new SrTypeOption("iso-cifs", "ISO library (SMB/CIFS)"));
        Types.Add(new SrTypeOption("nfs", "NFS VHD"));
        Types.Add(new SrTypeOption("smb", "SMB storage"));
        Types.Add(new SrTypeOption("iscsi", "iSCSI (LVM)"));
        SelectedType = Types[0];
        UpdateTypeFlags();
    }

    public ObservableCollection<SrTypeOption> Types { get; } = new();
    public ObservableCollection<IscsiIqnOption> IscsiIqns { get; } = new();
    public ObservableCollection<IscsiLunOption> IscsiLuns { get; } = new();

    [ObservableProperty] private SrTypeOption? _selectedType;
    [ObservableProperty] private bool _showIsoNfs;
    [ObservableProperty] private bool _showIsoCifs;
    [ObservableProperty] private bool _showNfs;
    [ObservableProperty] private bool _showSmb;
    [ObservableProperty] private bool _showIscsi;
    [ObservableProperty] private string _nameLabel = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _nfsPath = string.Empty;
    [ObservableProperty] private string _cifsLocation = string.Empty;
    [ObservableProperty] private string _cifsUsername = string.Empty;
    [ObservableProperty] private string _cifsPassword = string.Empty;
    [ObservableProperty] private string _smbPath = string.Empty;
    [ObservableProperty] private string _smbUsername = string.Empty;
    [ObservableProperty] private string _smbPassword = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProbeIqnsCommand))]
    private string _iscsiHost = string.Empty;

    [ObservableProperty] private string _iscsiPort = "3260";
    [ObservableProperty] private string _iscsiChapUser = string.Empty;
    [ObservableProperty] private string _iscsiChapPassword = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProbeLunsCommand))]
    private IscsiIqnOption? _selectedIscsiIqn;

    [ObservableProperty] private IscsiLunOption? _selectedIscsiLun;
    [ObservableProperty] private string _iscsiScsiId = string.Empty;
    [ObservableProperty] private bool _useGfs2;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProbeIqnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ProbeLunsCommand))]
    private bool _isProbing;

    public bool CanProbeIqns => ShowIscsi && !_probing && !string.IsNullOrWhiteSpace(IscsiHost);
    public bool CanProbeLuns => ShowIscsi && !_probing && SelectedIscsiIqn != null;
    public bool HasIscsiIqns => IscsiIqns.Count > 0;
    public bool HasIscsiLuns => IscsiLuns.Count > 0;

    partial void OnSelectedTypeChanged(SrTypeOption? value) => UpdateTypeFlags();

    partial void OnIscsiHostChanged(string value)
    {
        OnPropertyChanged(nameof(CanProbeIqns));
    }

    partial void OnSelectedIscsiIqnChanged(IscsiIqnOption? value)
    {
        OnPropertyChanged(nameof(CanProbeLuns));
        IscsiLuns.Clear();
        SelectedIscsiLun = null;
        IscsiScsiId = string.Empty;
        OnPropertyChanged(nameof(HasIscsiLuns));
    }

    partial void OnSelectedIscsiLunChanged(IscsiLunOption? value)
    {
        if (value != null)
            IscsiScsiId = value.Info.ScsiID ?? string.Empty;
    }

    partial void OnIsProbingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanProbeIqns));
        OnPropertyChanged(nameof(CanProbeLuns));
    }

    private void UpdateTypeFlags()
    {
        var id = SelectedType?.Id;
        ShowIsoNfs = id == "iso-nfs";
        ShowIsoCifs = id == "iso-cifs";
        ShowNfs = id == "nfs";
        ShowSmb = id == "smb";
        ShowIscsi = id == "iscsi";
        OnPropertyChanged(nameof(CanProbeIqns));
        OnPropertyChanged(nameof(CanProbeLuns));
    }

    [RelayCommand(CanExecute = nameof(CanProbeIqns))]
    private void ProbeIqns()
    {
        if (!ushort.TryParse(IscsiPort.Trim(), out var port))
            port = 3260;

        if (string.IsNullOrWhiteSpace(IscsiHost))
        {
            StatusMessage = "Enter an iSCSI target host or IP.";
            return;
        }

        SetProbing(true, $"Scanning IQNs on {IscsiHost.Trim()}…");
        IscsiIqns.Clear();
        IscsiLuns.Clear();
        SelectedIscsiIqn = null;
        SelectedIscsiLun = null;
        IscsiScsiId = string.Empty;
        OnPropertyChanged(nameof(HasIscsiIqns));
        OnPropertyChanged(nameof(HasIscsiLuns));

        ISCSIPopulateIQNsAction action = UseGfs2
            ? new Gfs2PopulateIQNsAction(
                _connection,
                IscsiHost.Trim(),
                port,
                IscsiChapUser?.Trim() ?? string.Empty,
                IscsiChapPassword ?? string.Empty)
            : new ISCSIPopulateIQNsAction(
                _connection,
                IscsiHost.Trim(),
                port,
                IscsiChapUser?.Trim() ?? string.Empty,
                IscsiChapPassword ?? string.Empty);

        action.Completed += a =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                SetProbing(false, string.Empty);
                if (!a.Succeeded)
                {
                    StatusMessage = a.Exception?.Message ?? "IQN scan failed.";
                    return;
                }

                var iqns = ((ISCSIPopulateIQNsAction)a).IQNs ?? Array.Empty<IScsiIqnInfo>();
                foreach (var iqn in iqns)
                    IscsiIqns.Add(new IscsiIqnOption(iqn));

                SelectedIscsiIqn = IscsiIqns.FirstOrDefault();
                OnPropertyChanged(nameof(HasIscsiIqns));
                StatusMessage = IscsiIqns.Count == 0
                    ? "No IQNs found."
                    : $"Found {IscsiIqns.Count} IQN(s). Select one, then scan LUNs.";
                ProbeIqnsCommand.NotifyCanExecuteChanged();
                ProbeLunsCommand.NotifyCanExecuteChanged();
            });
        };

        action.RunAsync();
        ProbeIqnsCommand.NotifyCanExecuteChanged();
        ProbeLunsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanProbeLuns))]
    private void ProbeLuns()
    {
        if (SelectedIscsiIqn == null)
        {
            StatusMessage = "Select a target IQN first.";
            return;
        }

        var info = SelectedIscsiIqn.Info;
        var host = string.IsNullOrWhiteSpace(info.IpAddress) ? IscsiHost.Trim() : info.IpAddress!;
        var port = info.Port == 0 ? (ushort.TryParse(IscsiPort.Trim(), out var p) ? p : (ushort)3260) : info.Port;
        var iqn = info.TargetIQN ?? string.Empty;

        SetProbing(true, $"Scanning LUNs for {iqn}…");
        IscsiLuns.Clear();
        SelectedIscsiLun = null;
        IscsiScsiId = string.Empty;
        OnPropertyChanged(nameof(HasIscsiLuns));

        ISCSIPopulateLunsAction action = UseGfs2
            ? new Gfs2PopulateLunsAction(
                _connection,
                host,
                port,
                iqn,
                IscsiChapUser?.Trim() ?? string.Empty,
                IscsiChapPassword ?? string.Empty)
            : new ISCSIPopulateLunsAction(
                _connection,
                host,
                port,
                iqn,
                IscsiChapUser?.Trim() ?? string.Empty,
                IscsiChapPassword ?? string.Empty);

        action.Completed += a =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                SetProbing(false, string.Empty);
                if (!a.Succeeded)
                {
                    StatusMessage = a.Exception?.Message ?? "LUN scan failed.";
                    return;
                }

                var luns = ((ISCSIPopulateLunsAction)a).LUNs ?? Array.Empty<ISCSIInfo>();
                foreach (var lun in luns)
                    IscsiLuns.Add(new IscsiLunOption(lun));

                SelectedIscsiLun = IscsiLuns.FirstOrDefault();
                OnPropertyChanged(nameof(HasIscsiLuns));
                StatusMessage = IscsiLuns.Count == 0
                    ? "No LUNs found."
                    : $"Found {IscsiLuns.Count} LUN(s). Select one and create the SR.";
                ProbeIqnsCommand.NotifyCanExecuteChanged();
                ProbeLunsCommand.NotifyCanExecuteChanged();
            });
        };

        action.RunAsync();
        ProbeIqnsCommand.NotifyCanExecuteChanged();
        ProbeLunsCommand.NotifyCanExecuteChanged();
    }

    private void SetProbing(bool probing, string message)
    {
        _probing = probing;
        IsProbing = probing;
        if (!string.IsNullOrEmpty(message))
            StatusMessage = message;
    }

    [RelayCommand]
    private void Create()
    {
        StatusMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(NameLabel))
        {
            StatusMessage = "Enter a name for the SR.";
            return;
        }

        Dictionary<string, string> dconf;
        SR.SRTypes type;
        string contentType;

        switch (SelectedType?.Id)
        {
            case "iso-nfs":
                if (string.IsNullOrWhiteSpace(NfsPath) || !NfsPath.Contains(':'))
                {
                    StatusMessage = "Enter an NFS ISO path like server:/path.";
                    return;
                }

                type = SR.SRTypes.iso;
                contentType = SR.Content_Type_ISO;
                dconf = new Dictionary<string, string>
                {
                    ["location"] = NfsPath.Trim(),
                    ["type"] = "nfs_iso"
                };
                break;

            case "iso-cifs":
            {
                var location = NormalizeCifsLocation(CifsLocation);
                if (string.IsNullOrWhiteSpace(location))
                {
                    StatusMessage = "Enter a CIFS path like \\\\server\\share or //server/share.";
                    return;
                }

                type = SR.SRTypes.iso;
                contentType = SR.Content_Type_ISO;
                dconf = new Dictionary<string, string>
                {
                    ["location"] = location,
                    ["type"] = "cifs"
                };

                // Split //server/share/path → location + iso_path
                var bits = location.Split('/');
                if (bits.Length > 4)
                {
                    dconf["location"] = $"//{bits[2]}/{bits[3]}";
                    dconf["iso_path"] = "/" + string.Join("/", bits.Skip(4));
                }

                if (!string.IsNullOrWhiteSpace(CifsUsername) || !string.IsNullOrWhiteSpace(CifsPassword))
                {
                    dconf["username"] = CifsUsername?.Trim() ?? string.Empty;
                    dconf["cifspassword"] = CifsPassword ?? string.Empty;
                }

                break;
            }

            case "nfs":
            {
                if (string.IsNullOrWhiteSpace(NfsPath) || !NfsPath.Contains(':'))
                {
                    StatusMessage = "Enter an NFS path like server:/path.";
                    return;
                }

                var parts = NfsPath.Trim().Split(new[] { ':' }, 2);
                type = SR.SRTypes.nfs;
                contentType = "user";
                dconf = new Dictionary<string, string>
                {
                    ["server"] = parts[0],
                    ["serverpath"] = parts.Length > 1 ? parts[1] : "/",
                    ["options"] = string.Empty
                };
                break;
            }

            case "smb":
            {
                if (string.IsNullOrWhiteSpace(SmbPath) || !SmbPath.Contains(':'))
                {
                    StatusMessage = "Enter an SMB path like server:/share.";
                    return;
                }

                var parts = SmbPath.Trim().Split(new[] { ':' }, 2);
                type = SR.SRTypes.smb;
                contentType = "user";
                dconf = new Dictionary<string, string>
                {
                    ["server"] = parts[0],
                    ["serverpath"] = parts.Length > 1 ? parts[1] : "/"
                };
                if (!string.IsNullOrWhiteSpace(SmbUsername) || !string.IsNullOrWhiteSpace(SmbPassword))
                {
                    dconf["username"] = SmbUsername?.Trim() ?? string.Empty;
                    dconf["password"] = SmbPassword ?? string.Empty;
                }

                break;
            }

            case "iscsi":
            {
                var iqn = SelectedIscsiIqn?.Info.TargetIQN?.Trim() ?? string.Empty;
                var scsiId = SelectedIscsiLun?.Info.ScsiID?.Trim()
                             ?? IscsiScsiId?.Trim()
                             ?? string.Empty;
                var targetHost = SelectedIscsiIqn?.Info.IpAddress;
                if (string.IsNullOrWhiteSpace(targetHost))
                    targetHost = IscsiHost.Trim();

                if (string.IsNullOrWhiteSpace(targetHost) || string.IsNullOrWhiteSpace(iqn)
                    || string.IsNullOrWhiteSpace(scsiId))
                {
                    StatusMessage = "Probe IQNs and LUNs (or enter host, IQN, and SCSI ID).";
                    return;
                }

                ushort port;
                if (SelectedIscsiIqn is { Info.Port: > 0 } selected)
                    port = selected.Info.Port;
                else if (!ushort.TryParse(IscsiPort.Trim(), out port))
                    port = 3260;

                type = UseGfs2 ? SR.SRTypes.gfs2 : SR.SRTypes.lvmoiscsi;
                contentType = "user";
                dconf = new Dictionary<string, string>();
                if (UseGfs2)
                    dconf["provider"] = "iscsi";
                dconf["target"] = targetHost!;
                dconf["port"] = port.ToString();
                dconf["targetIQN"] = iqn;
                dconf["SCSIid"] = scsiId;
                if (!string.IsNullOrWhiteSpace(IscsiChapUser))
                {
                    dconf["chapuser"] = IscsiChapUser.Trim();
                    dconf["chappassword"] = IscsiChapPassword ?? string.Empty;
                }

                break;
            }

            default:
                StatusMessage = "Select an SR type.";
                return;
        }

        var action = new SrCreateAction(
            _connection,
            _host,
            NameLabel.Trim(),
            Description.Trim(),
            type,
            contentType,
            dconf,
            smconf: new Dictionary<string, string>());

        ShellActionRunner.Run(action);
        _close();
    }

    private static string NormalizeCifsLocation(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var s = input.Trim().Replace('\\', '/');
        if (s.StartsWith("//", StringComparison.Ordinal))
            return s;
        if (s.StartsWith("/", StringComparison.Ordinal))
            return "/" + s; // unlikely; keep as-is after slash normalize
        // server/share → //server/share
        return "//" + s.TrimStart('/');
    }
}
