using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Core;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.Views;

namespace XcpNgCenter.Shell.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfigureDr))]
    [NotifyCanExecuteChangedFor(nameof(ConfigureDrCommand))]
    private bool _isDrEditorOpen;

    public bool ShowDrConfiguration => (SelectedInfraNode ?? _pinnedInfraNode) is
        { Kind: InfraNodeKind.Pool or InfraNodeKind.Host, Server.Connection.IsConnected: true };
    public bool CanConfigureDr => !_disposed && !IsDrEditorOpen && ResolveHaPool() != null;
    private void RefreshDrCommand()
    {
        OnPropertyChanged(nameof(ShowDrConfiguration)); OnPropertyChanged(nameof(CanConfigureDr));
        ConfigureDrCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanConfigureDr))]
    private async Task ConfigureDrAsync()
    {
        if (IsDrEditorOpen || ResolveHaPool() is not { } pool || GetMainWindow() is not { } owner) return;
        IsDrEditorOpen = true;
        try
        {
            var dialog = new DrEditorWindow { Title = $"Disaster recovery — {pool.Name()}" };
            dialog.DataContext = new DrEditorViewModel(new DrManagement(pool, message => StatusMessage = message),
                ShellConfirmPrompt.ConfirmAsync, dialog.Close);
            await dialog.ShowDialog(owner);
        }
        catch (Exception error)
        {
            StatusMessage = "Disaster recovery unavailable: " + error.Message;
            await ShellConfirmPrompt.AlertAsync("Disaster recovery unavailable", error.Message);
        }
        finally { IsDrEditorOpen = false; RefreshDrCommand(); }
    }
}
