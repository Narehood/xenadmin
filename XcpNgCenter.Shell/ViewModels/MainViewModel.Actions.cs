using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin;
using XenAdmin.Actions;
using XenAdmin.Actions.VMActions;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.Views;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<ActionBase, ActionLogRow> _logRows = new();
    private Window? _consolePopOut;

    public ObservableCollection<ActionLogRow> ActionLogRows { get; } = new();

    public bool HasActionLogRows => ActionLogRows.Count > 0;

    [ObservableProperty]
    private string _actionStatusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVmActionBar))]
    [NotifyPropertyChangedFor(nameof(CanStartVm))]
    [NotifyPropertyChangedFor(nameof(CanShutdownVm))]
    [NotifyPropertyChangedFor(nameof(CanRebootVm))]
    [NotifyPropertyChangedFor(nameof(CanSuspendVm))]
    [NotifyPropertyChangedFor(nameof(CanResumeVm))]
    [NotifyPropertyChangedFor(nameof(CanForceShutdownVm))]
    [NotifyPropertyChangedFor(nameof(CanForceRebootVm))]
    [NotifyPropertyChangedFor(nameof(CanEditVm))]
    [NotifyPropertyChangedFor(nameof(CanAttachIso))]
    [NotifyPropertyChangedFor(nameof(ShowPoolStorageActions))]
    [NotifyCanExecuteChangedFor(nameof(StartVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShutdownVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceShutdownVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(RebootVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceRebootVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(SuspendVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(AttachIsoCommand))]
    private VM? _selectedVm;

    [ObservableProperty]
    private bool _isConsolePoppedOut;

    public bool ShowVmActionBar => SelectedVm != null;

    public bool ShowPoolStorageActions =>
        SelectedInfraNode is { Kind: InfraNodeKind.Pool or InfraNodeKind.Host, Server.Connection.IsConnected: true };

    public bool CanStartVm => SelectedVm?.power_state is vm_power_state.Halted or vm_power_state.Suspended;

    public bool CanShutdownVm => SelectedVm?.power_state == vm_power_state.Running;

    public bool CanRebootVm => SelectedVm?.power_state == vm_power_state.Running;

    public bool CanSuspendVm => SelectedVm?.power_state == vm_power_state.Running;

    public bool CanResumeVm => SelectedVm?.power_state == vm_power_state.Suspended;

    public bool CanForceShutdownVm =>
        SelectedVm?.power_state is vm_power_state.Running or vm_power_state.Paused or vm_power_state.Suspended;

    public bool CanForceRebootVm =>
        SelectedVm?.power_state is vm_power_state.Running or vm_power_state.Paused;

    public bool CanEditVm => SelectedVm != null;

    public bool CanAttachIso => SelectedVm != null;

    public bool ShowEmbeddedConsole => HasConsoleFrame && !IsConsolePoppedOut;

    public bool ShowConsoleReattach => IsConsolePoppedOut;

    private void InitializeActionHistoryUi()
    {
        ActionLogRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasActionLogRows));
        ConnectionsManager.History.CollectionChanged += OnHistoryCollectionChanged;
        foreach (var action in ConnectionsManager.History.AsEnumerable().ToList())
            InsertLogRow(action, index: 0);
    }

    private void DisposeActionHistoryUi()
    {
        ConnectionsManager.History.CollectionChanged -= OnHistoryCollectionChanged;
        CloseConsolePopOut();
        ShellBootstrap.ActionHistory?.Dispose();
    }

    private void OnHistoryCollectionChanged(object? sender, CollectionChangeEventArgs e)
    {
        void Apply()
        {
            switch (e.Action)
            {
                case CollectionChangeAction.Add when e.Element is ActionBase added:
                    InsertLogRow(added, index: 0);
                    break;
                case CollectionChangeAction.Remove when e.Element is ActionBase removed:
                    RemoveLogRow(removed);
                    break;
                case CollectionChangeAction.Remove when e.Element is List<ActionBase> range:
                    foreach (var a in range)
                        RemoveLogRow(a);
                    break;
                case CollectionChangeAction.Refresh:
                    ActionLogRows.Clear();
                    _logRows.Clear();
                    foreach (var action in ConnectionsManager.History.AsEnumerable().Reverse())
                        InsertLogRow(action, ActionLogRows.Count);
                    break;
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void InsertLogRow(ActionBase action, int index)
    {
        if (_logRows.ContainsKey(action))
            return;
        var row = new ActionLogRow(action);
        _logRows[action] = row;
        if (index < 0 || index > ActionLogRows.Count)
            ActionLogRows.Add(row);
        else
            ActionLogRows.Insert(index, row);
    }

    private void RemoveLogRow(ActionBase action)
    {
        if (!_logRows.Remove(action, out var row))
            return;
        ActionLogRows.Remove(row);
    }

    private void RefreshSelectedVm()
    {
        var node = SelectedInfraNode ?? _pinnedInfraNode;
        SelectedVm = ResolveVm(node);
        OnPropertyChanged(nameof(ShowPoolStorageActions));
        OnPropertyChanged(nameof(ShowEmbeddedConsole));
        OnPropertyChanged(nameof(ShowConsoleReattach));
    }

    private static VM? ResolveVm(InfraTreeNode? node)
    {
        if (node is not { Kind: InfraNodeKind.Vm, OpaqueRef: { } opaque, Server.Connection: { IsConnected: true } conn })
            return null;
        return conn.Resolve(new XenRef<VM>(opaque));
    }

    private IXenConnection? SelectedConnection =>
        (SelectedInfraNode ?? _pinnedInfraNode)?.Server?.Connection
        ?? SelectedServer?.Connection
        ?? SelectedVm?.Connection;

    private Host? ResolveSelectedHost()
    {
        var node = SelectedInfraNode ?? _pinnedInfraNode;
        var conn = SelectedConnection;
        if (conn is not { IsConnected: true } || node == null)
            return null;

        if (node.Kind == InfraNodeKind.Host && node.OpaqueRef != null)
            return conn.Resolve(new XenRef<Host>(node.OpaqueRef));

        return Helpers.GetCoordinator(conn) ?? conn.Cache.Hosts.FirstOrDefault();
    }

    private void RunAction(AsyncAction action)
    {
        ShellActionRunner.Run(action, msg =>
        {
            ActionStatusMessage = msg;
            StatusMessage = msg;
        });
    }

    private static void NoHaWarning(VM _, bool __)
    {
    }

    private static void NoStartDiagnosis(VMStartAbstractAction _, Failure __)
    {
    }

    [RelayCommand]
    private void CancelAction(ActionLogRow? row)
    {
        if (row?.Action is { IsCompleted: false } action && action.CanCancel)
            action.Cancel();
    }

    [RelayCommand]
    private void DismissAction(ActionLogRow? row)
    {
        if (row?.Action is not { IsCompleted: true } action)
            return;
        ConnectionsManager.History.Remove(action);
    }

    [RelayCommand]
    private void DismissCompletedActions()
    {
        var done = ConnectionsManager.History.Where(a => a.IsCompleted).ToList();
        foreach (var action in done)
            ConnectionsManager.History.Remove(action);
    }

    [RelayCommand(CanExecute = nameof(CanStartVm))]
    private void StartVm()
    {
        if (SelectedVm == null)
            return;
        RunAction(new VMStartAction(SelectedVm, NoHaWarning, NoStartDiagnosis));
    }

    [RelayCommand(CanExecute = nameof(CanShutdownVm))]
    private void ShutdownVm()
    {
        if (SelectedVm == null)
            return;
        RunAction(new VMCleanShutdown(SelectedVm));
    }

    [RelayCommand(CanExecute = nameof(CanForceShutdownVm))]
    private void ForceShutdownVm()
    {
        if (SelectedVm == null)
            return;
        RunAction(new VMHardShutdown(SelectedVm));
    }

    [RelayCommand(CanExecute = nameof(CanRebootVm))]
    private void RebootVm()
    {
        if (SelectedVm == null)
            return;
        RunAction(new VMCleanReboot(SelectedVm));
    }

    [RelayCommand(CanExecute = nameof(CanForceRebootVm))]
    private void ForceRebootVm()
    {
        if (SelectedVm == null)
            return;
        RunAction(new VMHardReboot(SelectedVm));
    }

    [RelayCommand(CanExecute = nameof(CanSuspendVm))]
    private void SuspendVm()
    {
        if (SelectedVm == null)
            return;
        RunAction(new VMSuspendAction(SelectedVm));
    }

    [RelayCommand(CanExecute = nameof(CanResumeVm))]
    private void ResumeVm()
    {
        if (SelectedVm == null)
            return;
        RunAction(new VMResumeAction(SelectedVm, NoHaWarning, NoStartDiagnosis));
    }

    [RelayCommand]
    private async Task NewVmAsync()
    {
        var conn = SelectedConnection;
        if (conn is not { IsConnected: true })
        {
            StatusMessage = "Connect to a server before creating a VM.";
            return;
        }

        var owner = GetMainWindow();
        var wizard = new NewVmWizardWindow(conn);
        if (owner != null)
            await wizard.ShowDialog(owner);
        else
            wizard.Show();
    }

    [RelayCommand(CanExecute = nameof(CanEditVm))]
    private async Task EditVmAsync()
    {
        if (SelectedVm == null)
            return;

        var owner = GetMainWindow();
        var dialog = new VmEditWindow(SelectedVm);
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    [RelayCommand(CanExecute = nameof(CanAttachIso))]
    private async Task AttachIsoAsync()
    {
        if (SelectedVm == null)
            return;

        var owner = GetMainWindow();
        var dialog = new IsoAttachWindow(SelectedVm);
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    [RelayCommand]
    private void EjectIso()
    {
        if (SelectedVm == null)
            return;

        var cdrom = SelectedVm.FindVMCDROM();
        if (cdrom == null)
        {
            StatusMessage = "This VM has no CD/DVD drive.";
            return;
        }

        RunAction(new ChangeVMISOAction(SelectedVm.Connection, SelectedVm, vdi: null, cdrom));
    }

    [RelayCommand]
    private async Task NewSrAsync()
    {
        var conn = SelectedConnection;
        if (conn is not { IsConnected: true })
        {
            StatusMessage = "Connect to a server before creating storage.";
            return;
        }

        var host = ResolveSelectedHost();
        if (host == null)
        {
            StatusMessage = "No host available for SR creation.";
            return;
        }

        var owner = GetMainWindow();
        var wizard = new NewSrWizardWindow(conn, host);
        if (owner != null)
            await wizard.ShowDialog(owner);
        else
            wizard.Show();
    }

    [RelayCommand]
    private void RefreshStorage()
    {
        var conn = SelectedConnection;
        if (conn is not { IsConnected: true })
            return;

        var srs = conn.Cache.SRs
            .Where(sr => sr != null && !sr.IsToolsSR() && sr.PBDs.Count > 0)
            .OrderBy(sr => Helpers.GetName(sr), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (srs.Count == 0)
        {
            StatusMessage = "No attached SRs to refresh.";
            return;
        }

        foreach (var sr in srs)
            RunAction(new SrRefreshAction(sr));
    }

    [RelayCommand]
    private void PopOutConsole()
    {
        if (!HasConsoleFrame || IsConsolePoppedOut)
            return;

        var owner = GetMainWindow();
        var window = new ConsolePopOutWindow(this);
        _consolePopOut = window;
        IsConsolePoppedOut = true;
        OnPropertyChanged(nameof(ShowEmbeddedConsole));
        OnPropertyChanged(nameof(ShowConsoleReattach));

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_consolePopOut, window))
            {
                _consolePopOut = null;
                IsConsolePoppedOut = false;
                OnPropertyChanged(nameof(ShowEmbeddedConsole));
                OnPropertyChanged(nameof(ShowConsoleReattach));
            }
        };

        if (owner != null)
            window.Show(owner);
        else
            window.Show();
    }

    [RelayCommand]
    private void ReattachConsole()
    {
        CloseConsolePopOut();
    }

    [RelayCommand]
    private void SendCtrlAltDel()
    {
        // X11 keysyms: Control_L, Alt_L, Delete
        const int ControlL = 0xffe3;
        const int AltL = 0xffe9;
        const int Delete = 0xffff;

        _consoleSession.SendKey(true, ControlL);
        _consoleSession.SendKey(true, AltL);
        _consoleSession.SendKey(true, Delete);
        _consoleSession.SendKey(false, Delete);
        _consoleSession.SendKey(false, AltL);
        _consoleSession.SendKey(false, ControlL);
        ConsoleInputHint = "Sent Ctrl+Alt+Del to guest.";
    }

    private void CloseConsolePopOut()
    {
        var window = _consolePopOut;
        _consolePopOut = null;
        try
        {
            window?.Close();
        }
        catch
        {
            // Best-effort.
        }

        IsConsolePoppedOut = false;
        OnPropertyChanged(nameof(ShowEmbeddedConsole));
        OnPropertyChanged(nameof(ShowConsoleReattach));
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }
}
