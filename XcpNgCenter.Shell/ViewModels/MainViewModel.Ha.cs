using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.Views;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfigureHa))]
    [NotifyCanExecuteChangedFor(nameof(ConfigureHaCommand))]
    private bool _isHaEditorOpen;

    public bool ShowHaConfiguration => (SelectedInfraNode ?? _pinnedInfraNode) is
        { Kind: InfraNodeKind.Pool or InfraNodeKind.Host, Server.Connection.IsConnected: true };

    public bool CanConfigureHa => !_disposed && !IsHaEditorOpen && ResolveHaPool() != null;

    private Pool? ResolveHaPool()
    {
        var node = SelectedInfraNode ?? _pinnedInfraNode;
        if (node?.Server?.Connection is not { IsConnected: true } connection)
            return null;
        return node.Kind switch
        {
            InfraNodeKind.Pool => connection.Resolve(new XenRef<Pool>(node.OpaqueRef)),
            InfraNodeKind.Host when connection.Resolve(new XenRef<Host>(node.OpaqueRef)) != null
                => Helpers.GetPoolOfOne(connection),
            _ => null
        };
    }

    private void RefreshHaCommand()
    {
        OnPropertyChanged(nameof(ShowHaConfiguration));
        OnPropertyChanged(nameof(CanConfigureHa));
        ConfigureHaCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConfigureHa))]
    private Task ConfigureHaAsync() => ResolveHaPool() is { } pool
        ? OpenHaEditorAsync(pool) : Task.CompletedTask;

    private async Task OpenHaEditorAsync(Pool pool)
    {
        if (_disposed || IsHaEditorOpen || GetMainWindow() is not { } owner)
            return;

        IsHaEditorOpen = true;
        try
        {
            // Alerts supply their original pool; tree selection never redirects
            // this editor or its reviewed action after a dialog has opened.
            var connection = pool.Connection;
            var snapshot = HaManagement.Capture(pool);
            var dialog = new HaEditorWindow { Title = $"High availability — {pool.Name()}" };
            dialog.DataContext = new HaEditorViewModel(snapshot,
                (request, cancellation) => HaManagement.ReviewAsync(connection, snapshot, request, cancellation),
                async (request, review) =>
                {
                    var action = new ShellHaAction(connection, snapshot, request, review);
                    if (!await ShellActionRunner.RunAndWaitAsync(action, message => StatusMessage = message))
                        throw new InvalidOperationException(action.Exception?.Message ?? "The HA operation did not complete.");
                }, ShellConfirmPrompt.ConfirmAsync, dialog.Close);
            await dialog.ShowDialog(owner);
        }
        catch (Exception error)
        {
            StatusMessage = $"Unable to configure HA: {error.Message}";
            await ShellConfirmPrompt.AlertAsync("HA configuration unavailable", error.Message);
        }
        finally
        {
            IsHaEditorOpen = false;
            RefreshHaCommand();
            if (!_disposed)
                RefreshGeneralProperties(SelectedInfraNode ?? _pinnedInfraNode);
        }
    }
}
