using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.Views;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfigureAd))]
    [NotifyCanExecuteChangedFor(nameof(ConfigureAdCommand))]
    private bool _isAdEditorOpen;

    public bool ShowAdConfiguration => ShowHaConfiguration;
    public bool CanConfigureAd => !_disposed && !IsAdEditorOpen && ResolveHaPool() != null;
    private void RefreshAdCommand()
    {
        OnPropertyChanged(nameof(ShowAdConfiguration)); OnPropertyChanged(nameof(CanConfigureAd));
        ConfigureAdCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanConfigureAd))]
    private async Task ConfigureAdAsync()
    {
        if (ResolveHaPool() is not { } pool || GetMainWindow() is not { } owner) return;
        IsAdEditorOpen = true;
        try
        {
            var connection = pool.Connection;
            var snapshot = await AdManagement.LoadAsync(connection, pool.opaque_ref, pool.uuid);
            if (_disposed) return;
            var dialog = new AdEditorWindow();
            using var editor = new AdEditorViewModel(snapshot,
                (request, token) => AdManagement.ReviewAsync(connection, snapshot, request, token),
                async (request, review, credentials) =>
                {
                    var action = new AdAction(connection, snapshot, request, review, credentials);
                    if (!await ShellActionRunner.RunAndWaitAsync(action, message => StatusMessage = message))
                        throw new InvalidOperationException(action.Exception?.Message ?? AdManagement.RecoveryNotice);
                }, ShellConfirmPrompt.ConfirmAsync, dialog.Close);
            dialog.DataContext = editor;
            await dialog.ShowDialog(owner);
        }
        catch (Exception error)
        {
            StatusMessage = $"Directory access unavailable: {error.Message}";
            await ShellConfirmPrompt.AlertAsync("Directory access unavailable", error.Message);
        }
        finally { IsAdEditorOpen = false; RefreshAdCommand(); }
    }
}
