using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using Network = XenAPI.Network;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public sealed record NetworkUplinkOption(string Device, string Label);

public partial class NetworkEditorViewModel : ViewModelBase
{
    private readonly IXenConnection _connection;
    private readonly string? _reference;
    private readonly Action _close;
    private readonly Action<string>? _status;
    private readonly bool _preserveTopology;

    public NetworkEditorViewModel(IXenConnection connection, Network? network, Action close, Action<string>? status = null)
    {
        _connection = connection;
        _reference = network?.opaque_ref;
        _close = close;
        _status = status;
        Title = network == null ? "Add network" : "Edit network";
        NameLabel = network?.name_label ?? "New network";
        Description = network?.name_description ?? "";
        Tags = string.Join("\n", network?.tags ?? []);
        Automatic = network?.GetAutoPlug() ?? true;
        Mtu = (network?.MTU ?? Network.MTU_DEFAULT).ToString();
        var pifs = network == null ? [] : NetworkManagement.Pifs(network);
        var pif = pifs.FirstOrDefault();
        CurrentTopology = pif == null ? "Private network" : pif.VLAN < 0 ? $"{pif.device} · Untagged" : $"{pif.device} · VLAN {pif.VLAN}";
        External = pif != null;
        Vlan = pif?.VLAN.ToString() ?? "1";
        Uplinks = NetworkManagement.Uplinks(connection)
            .Select(p => new NetworkUplinkOption(p.device, $"{p.device} · {connection.Resolve(p.network)?.Name()}" )).ToList();
        SelectedUplink = Uplinks.FirstOrDefault(p => p.Device == pif?.device) ?? Uplinks.FirstOrDefault();
        TopologyNotice = network == null ? "" : NetworkManagement.TopologyError(network) ?? "";
        CanEditTopology = TopologyNotice.Length == 0;
        _preserveTopology = !CanEditTopology;
        MtuNotice = network == null ? "" : NetworkManagement.MtuError(network) ?? "";
        CanEditMtu = MtuNotice.Length == 0;
    }

    public string Title { get; }
    public string CurrentTopology { get; }
    public IReadOnlyList<NetworkUplinkOption> Uplinks { get; }
    public bool CanEditTopology { get; }
    public bool CanEditMtu { get; }
    public string TopologyNotice { get; }
    public string MtuNotice { get; }
    public bool HasTopologyNotice => TopologyNotice.Length > 0;
    public bool HasMtuNotice => MtuNotice.Length > 0;
    [ObservableProperty] private string _nameLabel = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _tags = "";
    [ObservableProperty] private bool _automatic;
    [ObservableProperty] private string _mtu = "1500";
    [ObservableProperty] private bool _external;
    [ObservableProperty] private string _vlan = "1";
    [ObservableProperty] private NetworkUplinkOption? _selectedUplink;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isSaving;

    public NetworkEdit Request => new(_reference, NameLabel, Description, Tags, Automatic, Mtu, External,
        SelectedUplink?.Device, Vlan, _preserveTopology);

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            if (!_connection.IsConnected) throw new InvalidOperationException("The server disconnected. Reconnect and try again.");
            var request = Request;
            var plan = NetworkManagement.PlanNetwork(_connection, request);
            if (plan.Current != null && (plan.TopologyChanged || plan.MtuChanged)
                && !await ShellConfirmPrompt.ConfirmAsync(new ShellConfirmRequest
                {
                    Title = "Change network configuration",
                    Message = $"Change the VLAN, uplink, or MTU of '{plan.Current.Name()}' across the pool? Network traffic may be interrupted.",
                    AcceptLabel = "Apply changes"
                })) return;
            var action = ShellNetworkAction.SaveNetwork(_connection, request);
            var succeeded = await ShellActionRunner.RunAndWaitAsync(action, message => { StatusMessage = message; _status?.Invoke(message); });
            if (succeeded) { IsSaving = false; _close(); }
            else StatusMessage = $"The change did not complete: {action.Exception?.Message ?? "cancelled"}. Refresh the network before retrying; some changes may have applied.";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsSaving = false; }
    }
}
