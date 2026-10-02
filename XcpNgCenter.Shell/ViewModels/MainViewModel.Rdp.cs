using CommunityToolkit.Mvvm.Input;
using XenAdmin;
using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.Views;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public partial class MainViewModel
{
    public bool CanOpenRdp => !_disposed && RdpTargetReview.IsRunningGuest(SelectedVm)
        && SelectedVm!.Connection is { IsConnected: true };

    [RelayCommand(CanExecute = nameof(CanOpenRdp))]
    private async Task OpenRdpAsync()
    {
        if (!CanOpenRdp || SelectedVm is not { } vm || GetMainWindow() is not { } owner) return;
        var review = new RdpTargetReview(vm.Connection, vm.opaque_ref, vm.uuid);
        var dialog = new RdpConnectWindow(IdentifierPrivacy.VmName(Helpers.GetName(vm)),
            IdentifierPrivacy.HideIpAddresses ? [] : RdpClientLauncher.GuestAddresses(vm));
        var endpoint = await dialog.ShowDialog<RdpEndpoint?>(owner);
        if (endpoint == null) return;
        // Selection, connection and cache identity must still match the reviewed guest.
        if (_disposed || !CanOpenRdp || !review.Matches(SelectedVm)
            || !review.Matches(review.Connection.Resolve(new XenRef<VM>(review.OpaqueRef))))
        {
            ActionStatusMessage = "The selected guest changed or disconnected. Reopen Remote Desktop to review its address.";
            return;
        }
        ActionStatusMessage = RdpClientLauncher.TryLaunch(endpoint, out var error)
            ? "Remote Desktop client opened. Complete the connection in that window."
            : error ?? "The Remote Desktop client could not be started.";
    }
}
