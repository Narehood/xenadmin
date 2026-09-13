using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.Views;
using Network = XenAPI.Network;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private bool _isNetworkActionBusy;
    [ObservableProperty] private bool _canAddNetwork;
    [ObservableProperty] private string _addNetworkLabel = "Add network";
    [ObservableProperty] private string _networkScopeNotice = "";

    private void RefreshNetworkCommands(InfraTreeNode? node)
    {
        var vm = ResolveVm(node);
        CanAddNetwork = node?.Server?.Connection is { IsConnected: true }
            && (node.Kind is InfraNodeKind.Host or InfraNodeKind.Pool || vm != null && NetworkManagement.VmError(vm) == null);
        AddNetworkLabel = node?.Kind == InfraNodeKind.Vm ? "Add interface" : "Add network";
        NetworkScopeNotice = node?.Kind == InfraNodeKind.Vm
            ? "Connect VM interfaces to a network below. Create or change VLANs on the host or pool Network tab."
            : "Networks are shared across the pool. Add a private network or a VLAN on a physical interface or bond.";
    }

    private void NetworkStatus(string message)
    {
        ActionStatusMessage = message;
        StatusMessage = message;
    }

    private async Task NetworkOperationAsync(Func<Task> operation)
    {
        if (IsNetworkActionBusy) return;
        IsNetworkActionBusy = true;
        try { await operation(); }
        catch (Exception ex) { await ShellConfirmPrompt.AlertAsync("Network operation failed", ex.Message); NetworkStatus(ex.Message); }
        finally
        {
            IsNetworkActionBusy = false;
            RefreshNetworkProperties(SelectedInfraNode ?? _pinnedInfraNode);
        }
    }

    private async Task ShowNetworkEditorAsync(Window dialog)
    {
        if (GetMainWindow() is { } owner) await dialog.ShowDialog(owner);
    }

    [RelayCommand]
    private Task AddNetworkAsync() => NetworkOperationAsync(async () =>
    {
        var node = SelectedInfraNode ?? _pinnedInfraNode;
        if (node?.Server?.Connection is not { IsConnected: true } connection) return;
        if (node.Kind == InfraNodeKind.Vm && ResolveVm(node) is { } vm)
        {
            NetworkManagement.Require(NetworkManagement.VmError(vm));
            await ShowNetworkEditorAsync(new VifEditorWindow(vm, null, NetworkStatus));
        }
        else if (node.Kind is InfraNodeKind.Host or InfraNodeKind.Pool)
            await ShowNetworkEditorAsync(new NetworkEditorWindow(connection, null, NetworkStatus));
    });

    private static IXenObject ResolveNetworkTarget(NetworkItemRow row)
    {
        if (row.Target?.Connection is not { IsConnected: true } connection)
            throw new InvalidOperationException("The server disconnected. Reconnect and try again.");
        return row.Target switch
        {
            Network n => connection.Resolve(new XenRef<Network>(n.opaque_ref))
                ?? throw new InvalidOperationException("The network is no longer available."),
            VIF v => connection.Resolve(new XenRef<VIF>(v.opaque_ref))
                ?? throw new InvalidOperationException("The interface was removed or replaced. Refresh and try again."),
            _ => throw new InvalidOperationException("Select a network or interface.")
        };
    }

    [RelayCommand]
    private Task EditNetworkAsync(NetworkItemRow row) => NetworkOperationAsync(async () =>
    {
        switch (ResolveNetworkTarget(row))
        {
            case Network network:
                NetworkManagement.Require(NetworkManagement.EditNetworkError(network));
                await ShowNetworkEditorAsync(new NetworkEditorWindow(network.Connection, network, NetworkStatus));
                break;
            case VIF vif:
                NetworkManagement.Require(NetworkManagement.VifError(vif));
                await ShowNetworkEditorAsync(new VifEditorWindow(vif.Connection.Resolve(vif.VM), vif, NetworkStatus));
                break;
        }
    });

    [RelayCommand]
    private Task RemoveNetworkAsync(NetworkItemRow row) => NetworkOperationAsync(async () =>
    {
        var target = ResolveNetworkTarget(row);
        NetworkManagement.Require(target is Network n ? NetworkManagement.RemoveNetworkError(n) : NetworkManagement.VifError((VIF)target));
        if (!await ShellConfirmPrompt.ConfirmAsync(new ShellConfirmRequest
            {
                Title = target is Network ? "Remove network" : "Remove VM interface",
                Message = target is Network network ? $"Remove '{network.Name()}' and its VLAN interfaces across the pool?"
                    : $"Remove interface {((VIF)target).device} from its VM? This disconnects traffic through the interface.",
                AcceptLabel = "Remove"
            })) return;
        var action = new ShellNetworkAction(target.Connection, target is Network ? "Remove network" : "Remove VM interface", (_, session) =>
        {
            switch (ResolveNetworkTarget(row))
            {
                case Network current:
                    NetworkManagement.Require(NetworkManagement.RemoveNetworkError(current));
                    new NetworkAction(current.Connection, current, false).RunSync(session);
                    break;
                case VIF current:
                    NetworkManagement.Require(NetworkManagement.VifError(current));
                    new DeleteVIFAction(current).RunSync(session);
                    break;
            }
        });
        if (!await ShellActionRunner.RunAndWaitAsync(action, NetworkStatus))
            await ShellConfirmPrompt.AlertAsync("Removal failed", action.Exception?.Message ?? "The operation was cancelled.");
    });

    [RelayCommand]
    private Task ToggleNetworkInterfaceAsync(NetworkItemRow row) => NetworkOperationAsync(async () =>
    {
        if (ResolveNetworkTarget(row) is not VIF vif) return;
        NetworkManagement.Require(NetworkManagement.VifError(vif, true));
        var disconnect = vif.currently_attached;
        if (disconnect && !await ShellConfirmPrompt.ConfirmAsync(new ShellConfirmRequest
            {
                Title = "Disconnect VM interface", Message = $"Disconnect interface {vif.device}? The VM will lose traffic through this interface.",
                AcceptLabel = "Disconnect"
            })) return;
        var action = new ShellNetworkAction(vif.Connection, disconnect ? "Disconnect VM interface" : "Connect VM interface", (_, session) =>
        {
            var current = (VIF)ResolveNetworkTarget(row);
            NetworkManagement.Require(NetworkManagement.VifError(current, true));
            if (current.currently_attached != disconnect) throw new InvalidOperationException("The interface connection state changed. Refresh and try again.");
            AsyncAction shared = disconnect ? new UnplugVIFAction(current) : new PlugVIFAction(current);
            shared.RunSync(session);
        });
        if (!await ShellActionRunner.RunAndWaitAsync(action, NetworkStatus))
            await ShellConfirmPrompt.AlertAsync("Interface operation failed", action.Exception?.Message ?? "The operation was cancelled.");
    });
}
