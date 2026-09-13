using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAPI;
using XcpNgCenter.Shell.Services;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public sealed record VmNetworkOption(string Reference, string Label);

public partial class VifEditorViewModel : ViewModelBase
{
    private readonly VM _vm;
    private readonly string? _reference;
    private readonly Action _close;
    private readonly Action<string>? _status;

    public VifEditorViewModel(VM vm, VIF? vif, Action close, Action<string>? status = null)
    {
        _vm = vm;
        _reference = vif?.opaque_ref;
        _close = close;
        _status = status;
        Title = vif == null ? "Add network interface" : $"Edit interface {vif.device}";
        Networks = NetworkManagement.VmNetworks(vm).Select(n =>
        {
            var pif = NetworkManagement.Pifs(n).FirstOrDefault();
            var suffix = pif == null ? "Private" : pif.VLAN < 0 ? pif.device : $"{pif.device} · VLAN {pif.VLAN}";
            return new VmNetworkOption(n.opaque_ref, $"{n.Name()} · {suffix}");
        }).ToList();
        SelectedNetwork = vif == null ? Networks.FirstOrDefault() : Networks.FirstOrDefault(n => n.Reference == vif.network.opaque_ref);
        Mac = vif?.MAC ?? "";
        Limit = vif?.qos_algorithm_type == VIF.RATE_LIMIT_QOS_VALUE;
        Rate = vif?.qos_algorithm_params.GetValueOrDefault(VIF.KBPS_QOS_PARAMS_KEY) ?? "";
        CanEditRate = vif == null || string.IsNullOrEmpty(vif.qos_algorithm_type) || Limit;
        Notice = vif == null ? "A running VM will connect the new interface if hot-plug is supported."
            : "Saving replaces this virtual interface and can interrupt its traffic. The device number and advanced settings are retained.";
    }

    public string Title { get; }
    public string Notice { get; }
    public bool CanEditRate { get; }
    public IReadOnlyList<VmNetworkOption> Networks { get; }
    [ObservableProperty] private VmNetworkOption? _selectedNetwork;
    [ObservableProperty] private string _mac = "";
    [ObservableProperty] private bool _limit;
    [ObservableProperty] private string _rate = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isSaving;

    public VifEdit Request => new(_reference, SelectedNetwork?.Reference, Mac, Limit, Rate);

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            if (!_vm.Connection.IsConnected) throw new InvalidOperationException("The server disconnected. Reconnect and try again.");
            var request = Request;
            var descriptor = NetworkManagement.PlanVif(_vm, request);
            var current = _reference == null ? null : _vm.Connection.Resolve(new XenRef<VIF>(_reference));
            if (current != null && !NetworkManagement.VifSettingsChanged(current, descriptor))
            {
                IsSaving = false;
                _close();
                return;
            }
            if (_reference != null && _vm.Connection.Resolve(new XenRef<VIF>(_reference))?.currently_attached == true
                && !await ShellConfirmPrompt.ConfirmAsync(new ShellConfirmRequest
                {
                    Title = "Edit connected interface", Message = "Saving disconnects and replaces this interface. The VM may lose network access until the replacement connects.",
                    AcceptLabel = "Save interface"
                })) return;
            var action = ShellNetworkAction.SaveVif(_vm, request);
            var succeeded = await ShellActionRunner.RunAndWaitAsync(action, message => { StatusMessage = message; _status?.Invoke(message); });
            if (succeeded)
            {
                if (action.RebootRequired) await ShellConfirmPrompt.AlertAsync("VM restart required", action.Description);
                IsSaving = false;
                _close();
            }
            else StatusMessage = $"The change did not complete: {action.Exception?.Message ?? "cancelled"}. Refresh the VM before retrying; the old interface may already have been removed.";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsSaving = false; }
    }
}
