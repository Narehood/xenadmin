using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class NewSrWizardWindow : Window
{
    public NewSrWizardWindow()
    {
        InitializeComponent();
    }

    public NewSrWizardWindow(IXenConnection connection, Host host) : this()
    {
        DataContext = new NewSrWizardViewModel(connection, host, Close);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}

public sealed record SrTypeOption(string Id, string Label);

public partial class NewSrWizardViewModel : ViewModelBase
{
    private readonly IXenConnection _connection;
    private readonly Host _host;
    private readonly Action _close;

    public NewSrWizardViewModel(IXenConnection connection, Host host, Action close)
    {
        _connection = connection;
        _host = host;
        _close = close;
        Types.Add(new SrTypeOption("iso-nfs", "ISO library (NFS)"));
        Types.Add(new SrTypeOption("nfs", "NFS VHD"));
        Types.Add(new SrTypeOption("iscsi", "iSCSI"));
        SelectedType = Types[0];
        UpdateTypeFlags();
    }

    public ObservableCollection<SrTypeOption> Types { get; } = new();

    [ObservableProperty] private SrTypeOption? _selectedType;
    [ObservableProperty] private bool _showIsoNfs;
    [ObservableProperty] private bool _showNfs;
    [ObservableProperty] private bool _showIscsi;
    [ObservableProperty] private string _nameLabel = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _nfsPath = string.Empty;
    [ObservableProperty] private string _iscsiHost = string.Empty;
    [ObservableProperty] private string _iscsiPort = "3260";
    [ObservableProperty] private string _iscsiIqn = string.Empty;
    [ObservableProperty] private string _iscsiScsiId = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    partial void OnSelectedTypeChanged(SrTypeOption? value) => UpdateTypeFlags();

    private void UpdateTypeFlags()
    {
        var id = SelectedType?.Id;
        ShowIsoNfs = id == "iso-nfs";
        ShowNfs = id == "nfs";
        ShowIscsi = id == "iscsi";
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

            case "iscsi":
                if (string.IsNullOrWhiteSpace(IscsiHost) || string.IsNullOrWhiteSpace(IscsiIqn)
                    || string.IsNullOrWhiteSpace(IscsiScsiId))
                {
                    StatusMessage = "Enter iSCSI host, IQN, and SCSI ID (probe with legacy Center if needed).";
                    return;
                }

                if (!ushort.TryParse(IscsiPort.Trim(), out var port))
                    port = 3260;

                type = SR.SRTypes.lvmoiscsi;
                contentType = "user";
                dconf = new Dictionary<string, string>
                {
                    ["target"] = IscsiHost.Trim(),
                    ["port"] = port.ToString(),
                    ["targetIQN"] = IscsiIqn.Trim(),
                    ["SCSIid"] = IscsiScsiId.Trim()
                };
                break;

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
}
