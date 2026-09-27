using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Network;
using XcpNgCenter.Shell.Services;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public partial class SriovNetworkViewModel : ViewModelBase
{
    private readonly IXenConnection _connection;
    private readonly Action _close;
    private readonly Action<string> _status;

    public SriovNetworkViewModel(IXenConnection connection, Action close, Action<string> status)
    {
        _connection = connection;
        _close = close;
        _status = status;
        Refresh();
    }

    [ObservableProperty] private string _nameLabel = "New SR-IOV network";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _automatic;
    [ObservableProperty] private IReadOnlyList<SriovUplinkOption> _uplinks = [];
    [ObservableProperty] private SriovUplinkOption? _selectedUplink;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isSaving;
    public string SelectionNotice => SelectedUplink?.Error ?? (SelectedUplink == null
        ? "No SR-IOV-capable physical NIC is advertised by the pool coordinator."
        : "This NIC is available on every pool host. Creating SR-IOV may interrupt traffic and require host restarts.");
    public bool CanCreate => !IsSaving && SelectedUplink is { Error: null } && !string.IsNullOrWhiteSpace(NameLabel);

    partial void OnSelectedUplinkChanged(SriovUplinkOption? value)
    {
        OnPropertyChanged(nameof(SelectionNotice));
        OnPropertyChanged(nameof(CanCreate));
    }
    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnNameLabelChanged(string value) => OnPropertyChanged(nameof(CanCreate));

    [RelayCommand]
    private void Refresh()
    {
        if (IsSaving) return;
        var device = SelectedUplink?.Device;
        Uplinks = SriovNetworkManagement.Uplinks(_connection);
        SelectedUplink = Uplinks.FirstOrDefault(u => u.Device == device)
            ?? Uplinks.FirstOrDefault(u => u.Error == null) ?? Uplinks.FirstOrDefault();
        OnPropertyChanged(nameof(SelectionNotice));
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!CanCreate) return;
        IsSaving = true;
        try
        {
            if (!_connection.IsConnected) throw new InvalidOperationException("The server disconnected. Reconnect and try again.");
            var request = new SriovNetworkRequest(NameLabel, Description, Automatic, SelectedUplink!.Targets.ToArray());
            var plan = SriovNetworkManagement.Plan(_connection, request);
            var hostNames = plan.Pifs.Select(p => IdentifierPrivacy.ServerName(_connection.Resolve(p.host).Name()));
            if (!await ShellConfirmPrompt.ConfirmAsync(new ShellConfirmRequest
            {
                Title = "Create SR-IOV network",
                Message = $"Create '{request.Name.Trim()}' on {SelectedUplink.Device} across {string.Join(", ", hostNames)}? "
                    + "Network traffic may be interrupted. Some NICs require a planned host restart. VMs using SR-IOV cannot use live migration, suspend, or memory checkpoints. No host will be restarted automatically.",
                AcceptLabel = "Create SR-IOV network"
            })) return;
            var action = new CreateShellSriovNetworkAction(_connection, request);
            var succeeded = await ShellActionRunner.RunAndWaitAsync(action, message => { StatusMessage = message; _status(message); });
            if (succeeded)
            {
                _status(action.CompletionNotice);
                IsSaving = false;
                _close();
            }
            else StatusMessage = $"Provisioning did not complete: {action.Exception?.Message ?? "cancelled"}. Some hosts may have changed. Refresh and inspect the pool before retrying.";
        }
        catch (Exception exception) { StatusMessage = exception.Message; }
        finally { IsSaving = false; }
    }
}
