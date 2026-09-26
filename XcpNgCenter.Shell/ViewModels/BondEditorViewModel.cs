using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using Network = XenAPI.Network;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public partial class BondMemberOption : ObservableObject
{
    public BondMemberOption(PIF pif)
    {
        Reference = pif.opaque_ref;
        Label = $"{pif.device} · {pif.Connection.Resolve(pif.network)?.Name()} · MTU {pif.MTU}";
        Error = BondManagement.CandidateError(pif) ?? "";
    }
    public string Reference { get; }
    public string Label { get; }
    public string Error { get; }
    public bool IsAvailable => Error.Length == 0;
    public bool HasError => !IsAvailable;
    [ObservableProperty] private bool _isSelected;
}

public partial class BondEditorViewModel : ViewModelBase
{
    private readonly IXenConnection _connection;
    private readonly string? _networkReference;
    private readonly Action _close;
    private readonly Action<string>? _status;

    public BondEditorViewModel(IXenConnection connection, Network? network, Action close, Action<string>? status = null)
    {
        _connection = connection;
        _networkReference = network?.opaque_ref;
        _close = close;
        _status = status;
        IsCreating = network == null;
        Title = IsCreating ? "Create NIC bond" : "Change bond mode";
        NameLabel = network?.Name() ?? "New bond";
        Members = IsCreating ? BondManagement.Candidates(connection).Select(p => new BondMemberOption(p)).ToList() : [];
        var currentBond = network == null ? null : NetworkManagement.Pifs(network)
            .SelectMany(p => p.bond_master_of).Select(connection.Resolve).FirstOrDefault(b => b != null);
        SelectedMode = Modes.FirstOrDefault(m => m.Mode == currentBond?.mode
            && (m.Mode != bond_mode.lacp || m.Hashing == currentBond.HashingAlgoritm())) ?? Modes[0];
        MembersSummary = currentBond == null ? "" : string.Join(", ", connection.ResolveAll(currentBond.slaves).Select(p => p.device));
        if (!IsCreating) StatusMessage = BondManagement.ExistingError(network!) ?? "";
        else if (!Members.Any(m => m.IsAvailable)) StatusMessage = "No unused NICs are available across every host. Review the reasons below.";
    }

    public string Title { get; }
    public bool IsCreating { get; }
    public string MembersSummary { get; }
    public IReadOnlyList<BondMemberOption> Members { get; }
    public IReadOnlyList<BondModeOption> Modes => BondManagement.Modes;
    [ObservableProperty] private string _nameLabel = "";
    [ObservableProperty] private string _mtu = "1500";
    [ObservableProperty] private bool _automatic;
    [ObservableProperty] private BondModeOption? _selectedMode;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isSaving;
    public bool IsLacp => SelectedMode?.Mode == bond_mode.lacp;
    partial void OnSelectedModeChanged(BondModeOption? value) => OnPropertyChanged(nameof(IsLacp));

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            if (!_connection.IsConnected) throw new InvalidOperationException("The server disconnected. Reconnect and try again.");
            var mode = SelectedMode ?? throw new InvalidOperationException("Select a bond mode.");
            Func<ShellBondAction> createAction;
            string message;
            if (IsCreating)
            {
                var request = new BondCreateRequest(NameLabel, Members.Where(m => m.IsSelected).Select(m => m.Reference).ToArray(), Mtu, mode, Automatic);
                var plan = BondManagement.PlanCreate(_connection, request);
                message = $"Create '{request.Name.Trim()}' from {string.Join(", ", plan.CoordinatorMembers.Select(p => p.device))} on every host in this pool using {mode.Label}? Physical switch configuration must match the selected mode.";
                createAction = () => ShellBondAction.Create(_connection, request, plan.Snapshot);
            }
            else
            {
                var plan = BondManagement.PlanExisting(_connection, _networkReference!, mode);
                if (plan.Bonds.All(b => b.mode == mode.Mode && (mode.Mode != bond_mode.lacp || b.HashingAlgoritm() == mode.Hashing)))
                {
                    IsSaving = false;
                    _close();
                    return;
                }
                message = $"Change '{plan.Network.Name()}' to {mode.Label} on every host in the pool? Physical switch configuration must match the selected mode.";
                createAction = () => ShellBondAction.SetMode(_connection, _networkReference!, mode, plan.Snapshot);
            }
            if (!await ShellConfirmPrompt.ConfirmAsync(new ShellConfirmRequest
                {
                    Title = Title, Message = message + " Changes are applied sequentially; a failure can leave only some hosts changed.",
                    AcceptLabel = IsCreating ? "Create bond" : "Change mode"
                })) return;
            var action = createAction();
            if (await ShellActionRunner.RunAndWaitAsync(action, value => { StatusMessage = value; _status?.Invoke(value); }))
            {
                IsSaving = false;
                _close();
            }
            else StatusMessage = $"The bond change did not complete: {action.Exception?.Message ?? "cancelled"}. Refresh the pool before retrying; some changes may have applied.";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsSaving = false; }
    }
}
