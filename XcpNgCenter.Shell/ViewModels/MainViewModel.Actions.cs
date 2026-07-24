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
    [NotifyPropertyChangedFor(nameof(CanPauseVm))]
    [NotifyPropertyChangedFor(nameof(CanUnpauseVm))]
    [NotifyPropertyChangedFor(nameof(ShowSuspendToggle))]
    [NotifyPropertyChangedFor(nameof(SuspendToggleLabel))]
    [NotifyPropertyChangedFor(nameof(ShowPauseToggle))]
    [NotifyPropertyChangedFor(nameof(PauseToggleLabel))]
    [NotifyPropertyChangedFor(nameof(CanForceShutdownVm))]
    [NotifyPropertyChangedFor(nameof(CanForceRebootVm))]
    [NotifyPropertyChangedFor(nameof(CanEditVm))]
    [NotifyPropertyChangedFor(nameof(CanCloneVm))]
    [NotifyPropertyChangedFor(nameof(CanCopyVm))]
    [NotifyPropertyChangedFor(nameof(CanExportVm))]
    [NotifyPropertyChangedFor(nameof(CanMigrateVm))]
    [NotifyPropertyChangedFor(nameof(CanCrossPoolMigrateVm))]
    [NotifyPropertyChangedFor(nameof(CanMoveVm))]
    [NotifyPropertyChangedFor(nameof(CanDeleteVm))]
    [NotifyPropertyChangedFor(nameof(CanAttachIso))]
    [NotifyPropertyChangedFor(nameof(CanEjectIso))]
    [NotifyPropertyChangedFor(nameof(ShowConsoleIsoBar))]
    [NotifyPropertyChangedFor(nameof(ShowPoolStorageActions))]
    [NotifyPropertyChangedFor(nameof(CanImportExportVm))]
    [NotifyCanExecuteChangedFor(nameof(StartVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShutdownVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceShutdownVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(RebootVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceRebootVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(SuspendVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnpauseVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleSuspendVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloneVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(MigrateVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(CrossPoolMigrateVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteVmCommand))]
    [NotifyCanExecuteChangedFor(nameof(AttachIsoCommand))]
    [NotifyCanExecuteChangedFor(nameof(EjectIsoCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyConsoleIsoCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportExportVmCommand))]
    private VM? _selectedVm;

    [ObservableProperty]
    private bool _isConsolePoppedOut;

    private bool _suppressConsoleIsoSelection;

    public ObservableCollection<IsoOption> ConsoleIsoOptions { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyConsoleIsoCommand))]
    private IsoOption? _selectedConsoleIso;

    [ObservableProperty]
    private string _attachedIsoLabel = "Empty — no ISO inserted";

    [ObservableProperty]
    private bool _hasConsoleIsoOptions;

    public bool ShowVmActionBar => SelectedVm != null;

    public bool ShowConsoleIsoBar => SelectedVm != null;

    public bool ShowPoolStorageActions => SelectedConnection is { IsConnected: true };

    public bool CanStartVm => SelectedVm?.power_state is vm_power_state.Halted or vm_power_state.Suspended;

    public bool CanShutdownVm => SelectedVm?.power_state == vm_power_state.Running;

    public bool CanRebootVm => SelectedVm?.power_state == vm_power_state.Running;

    public bool CanSuspendVm => SelectedVm?.power_state == vm_power_state.Running;

    public bool CanResumeVm => SelectedVm?.power_state == vm_power_state.Suspended;

    public bool CanPauseVm => SelectedVm?.power_state == vm_power_state.Running;

    public bool CanUnpauseVm => SelectedVm?.power_state == vm_power_state.Paused;

    /// <summary>Single toolbar control that Suspends when running or Resumes when suspended.</summary>
    public bool ShowSuspendToggle => CanSuspendVm || CanResumeVm;

    public string SuspendToggleLabel => CanResumeVm ? "Resume" : "Suspend";

    /// <summary>Single toolbar control that Pauses when running or Unpauses when paused.</summary>
    public bool ShowPauseToggle => CanPauseVm || CanUnpauseVm;

    public string PauseToggleLabel => CanUnpauseVm ? "Unpause" : "Pause";

    public bool CanForceShutdownVm =>
        SelectedVm?.power_state is vm_power_state.Running or vm_power_state.Paused or vm_power_state.Suspended;

    public bool CanForceRebootVm =>
        SelectedVm?.power_state is vm_power_state.Running or vm_power_state.Paused;

    public bool CanEditVm => SelectedVm != null;

    public bool CanCloneVm =>
        SelectedVm is { is_a_template: false, Locked: false, allowed_operations: { } ops }
        && ops.Contains(vm_operations.clone);

    public bool CanCopyVm =>
        SelectedVm is { is_a_template: false, Locked: false, power_state: not vm_power_state.Suspended, allowed_operations: { } ops }
        && (ops.Contains(vm_operations.copy) || ops.Contains(vm_operations.clone));

    public bool CanExportVm =>
        SelectedVm != null && VmExportViewModel.CanExport(SelectedVm);

    public bool CanImportExportVm => SelectedConnection is { IsConnected: true };

    public bool CanMigrateVm =>
        SelectedVm is { is_a_template: false, Locked: false, power_state: vm_power_state.Running, allowed_operations: { } ops } vm
        && (
            (ops.Contains(vm_operations.pool_migrate)
             && vm.Connection.Cache.Hosts.Count(h => h.enabled && h.IsLive()) > 1)
            || (ops.Contains(vm_operations.migrate_send)
                && vm.SRs().All(sr => sr != null && !sr.HBALunPerVDI())
                && !ShellStoragePicker.HasCbtEnabledDisks(vm)
                && !Helpers.FeatureForbidden(vm.Connection, Host.RestrictCrossPoolMigrate)
                && vm.Connection.Cache.Hosts.Count(h => h.enabled && h.IsLive()
                                                       && (vm.resident_on == null
                                                           || h.opaque_ref != vm.resident_on.opaque_ref)) > 0)
        );

    public bool CanCrossPoolMigrateVm =>
        SelectedVm is { is_a_template: false, Locked: false, allowed_operations: { } ops } vm
        && ops.Contains(vm_operations.migrate_send)
        && vm.SRs().All(sr => sr != null && !sr.HBALunPerVDI())
        && !ShellStoragePicker.HasCbtEnabledDisks(vm)
        && !Helpers.FeatureForbidden(vm.Connection, Host.RestrictCrossPoolMigrate)
        && ConnectionsManager.XenConnectionsCopy.Count(c => c is { IsConnected: true }) >= 1
        && ShellStoragePicker.HasEligibleMigrateSendHosts(vm);

    public bool CanMoveVm =>
        SelectedVm is { is_a_template: false, Locked: false, power_state: vm_power_state.Halted } vm
        && !ShellStoragePicker.HasCbtEnabledDisks(vm)
        && (vm.CanBeMoved() || CanPreferMigrateSendMove(vm));

    public bool CanDeleteVm =>
        SelectedVm is { is_a_template: false, Locked: false, power_state: vm_power_state.Halted, allowed_operations: { } ops }
        && ops.Contains(vm_operations.destroy);

    /// <summary>
    /// Matches WinForms <c>CrossPoolMigrateCommand.CanRun</c> (migrate_send + non–LUN-per-VDI SRs).
    /// </summary>
    private static bool CanUseMigrateSend(VM vm) =>
        vm.allowed_operations?.Contains(vm_operations.migrate_send) == true
        && vm.SRs().All(sr => sr != null && !sr.HBALunPerVDI());

    /// <summary>
    /// WinForms <c>MoveVMCommand</c>: open migrate_send Move wizard when licensed, CBT-clear,
    /// and at least one eligible destination host exists; else fall back to simple Move dialog.
    /// </summary>
    private static bool CanPreferMigrateSendMove(VM vm) =>
        CanUseMigrateSend(vm)
        && !Helpers.FeatureForbidden(vm.Connection, Host.RestrictCrossPoolMigrate)
        && ShellStoragePicker.HasEligibleMigrateSendHosts(vm);

    public bool CanAttachIso => SelectedVm != null;

    public bool CanEjectIso => SelectedVm != null && ShellIsoLibrary.GetAttachedIso(SelectedVm) != null;

    public bool CanApplyConsoleIso =>
        SelectedVm != null
        && SelectedConsoleIso != null
        && (ShellIsoLibrary.GetAttachedIso(SelectedVm)?.opaque_ref != SelectedConsoleIso.Vdi.opaque_ref);

    public bool ShowVmContextActions => SelectedVm != null;

    public bool ShowPoolContextActions =>
        SelectedInfraNode is { Kind: InfraNodeKind.Pool or InfraNodeKind.Host, Server.Connection.IsConnected: true };

    public bool CanAddServer => true;

    public bool CanDisconnectSelected
    {
        get
        {
            var server = SelectedInfraNode?.Server ?? SelectedServer;
            return server is { IsConnected: true, Connection: not null };
        }
    }

    public bool CanCancelConnectSelected
    {
        get
        {
            var server = SelectedInfraNode?.Server ?? SelectedServer;
            return server is { IsConnecting: true };
        }
    }

    public bool CanReconnectSelected
    {
        get
        {
            var server = SelectedInfraNode?.Server ?? SelectedServer;
            return server is { IsDisconnected: true };
        }
    }

    public bool CanRemoveSelected
    {
        get
        {
            var server = SelectedInfraNode?.Server ?? SelectedServer;
            return server != null;
        }
    }

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
        foreach (var row in _logRows.Values)
            row.Dispose();
        _logRows.Clear();
        ActionLogRows.Clear();
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
                    foreach (var existing in _logRows.Values)
                        existing.Dispose();
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
        row.Dispose();
        ActionLogRows.Remove(row);
    }

    private void RefreshSelectedVm()
    {
        var node = SelectedInfraNode ?? _pinnedInfraNode;
        var vm = ResolveVm(node);
        // Cache updates mutate the same VM instance in place — always re-notify Can*.
        SelectedVm = vm;
        RefreshConsoleIsoOptions();
        NotifyVmPowerCanExecuteChanged();
        OnPropertyChanged(nameof(ShowPoolStorageActions));
        OnPropertyChanged(nameof(ShowVmContextActions));
        OnPropertyChanged(nameof(ShowPoolContextActions));
        OnPropertyChanged(nameof(CanDisconnectSelected));
        OnPropertyChanged(nameof(CanCancelConnectSelected));
        OnPropertyChanged(nameof(CanReconnectSelected));
        OnPropertyChanged(nameof(CanRemoveSelected));
        OnPropertyChanged(nameof(ShowEmbeddedConsole));
        OnPropertyChanged(nameof(ShowConsoleReattach));
    }

    private void RefreshConsoleIsoOptions()
    {
        _suppressConsoleIsoSelection = true;
        try
        {
            ConsoleIsoOptions.Clear();
            SelectedConsoleIso = null;
            AttachedIsoLabel = ShellIsoLibrary.FormatAttachedLabel(SelectedVm);
            if (SelectedVm == null)
            {
                HasConsoleIsoOptions = false;
                return;
            }

            foreach (var iso in ShellIsoLibrary.Enumerate(SelectedVm))
                ConsoleIsoOptions.Add(iso);

            HasConsoleIsoOptions = ConsoleIsoOptions.Count > 0;
            var attached = ShellIsoLibrary.GetAttachedIso(SelectedVm);
            if (attached != null)
            {
                SelectedConsoleIso = ConsoleIsoOptions.FirstOrDefault(o =>
                    o.Vdi.opaque_ref == attached.opaque_ref);
            }
        }
        finally
        {
            _suppressConsoleIsoSelection = false;
        }

        OnPropertyChanged(nameof(CanEjectIso));
        OnPropertyChanged(nameof(CanApplyConsoleIso));
        ApplyConsoleIsoCommand.NotifyCanExecuteChanged();
        EjectIsoCommand.NotifyCanExecuteChanged();
    }

    private void NotifyVmPowerCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanStartVm));
        OnPropertyChanged(nameof(CanShutdownVm));
        OnPropertyChanged(nameof(CanRebootVm));
        OnPropertyChanged(nameof(CanSuspendVm));
        OnPropertyChanged(nameof(CanResumeVm));
        OnPropertyChanged(nameof(CanPauseVm));
        OnPropertyChanged(nameof(CanUnpauseVm));
        OnPropertyChanged(nameof(ShowSuspendToggle));
        OnPropertyChanged(nameof(SuspendToggleLabel));
        OnPropertyChanged(nameof(ShowPauseToggle));
        OnPropertyChanged(nameof(PauseToggleLabel));
        OnPropertyChanged(nameof(CanForceShutdownVm));
        OnPropertyChanged(nameof(CanForceRebootVm));
        OnPropertyChanged(nameof(CanEditVm));
        OnPropertyChanged(nameof(CanCloneVm));
        OnPropertyChanged(nameof(CanCopyVm));
        OnPropertyChanged(nameof(CanExportVm));
        OnPropertyChanged(nameof(CanMigrateVm));
        OnPropertyChanged(nameof(CanCrossPoolMigrateVm));
        OnPropertyChanged(nameof(CanMoveVm));
        OnPropertyChanged(nameof(CanDeleteVm));
        OnPropertyChanged(nameof(CanAttachIso));
        OnPropertyChanged(nameof(CanEjectIso));
        OnPropertyChanged(nameof(CanApplyConsoleIso));
        OnPropertyChanged(nameof(ShowVmActionBar));
        OnPropertyChanged(nameof(ShowConsoleIsoBar));
        OnPropertyChanged(nameof(ShowVmContextActions));
        OnPropertyChanged(nameof(ShowPoolContextActions));
        OnPropertyChanged(nameof(ShowPoolStorageActions));
        OnPropertyChanged(nameof(CanDisconnectSelected));
        OnPropertyChanged(nameof(CanCancelConnectSelected));
        OnPropertyChanged(nameof(CanReconnectSelected));
        OnPropertyChanged(nameof(CanRemoveSelected));
        OnPropertyChanged(nameof(CanImportExportVm));
        StartVmCommand.NotifyCanExecuteChanged();
        ShutdownVmCommand.NotifyCanExecuteChanged();
        ForceShutdownVmCommand.NotifyCanExecuteChanged();
        RebootVmCommand.NotifyCanExecuteChanged();
        ForceRebootVmCommand.NotifyCanExecuteChanged();
        SuspendVmCommand.NotifyCanExecuteChanged();
        ResumeVmCommand.NotifyCanExecuteChanged();
        PauseVmCommand.NotifyCanExecuteChanged();
        UnpauseVmCommand.NotifyCanExecuteChanged();
        ToggleSuspendVmCommand.NotifyCanExecuteChanged();
        TogglePauseVmCommand.NotifyCanExecuteChanged();
        EditVmCommand.NotifyCanExecuteChanged();
        CloneVmCommand.NotifyCanExecuteChanged();
        CopyVmCommand.NotifyCanExecuteChanged();
        ExportVmCommand.NotifyCanExecuteChanged();
        MigrateVmCommand.NotifyCanExecuteChanged();
        CrossPoolMigrateVmCommand.NotifyCanExecuteChanged();
        MoveVmCommand.NotifyCanExecuteChanged();
        DeleteVmCommand.NotifyCanExecuteChanged();
        AttachIsoCommand.NotifyCanExecuteChanged();
        EjectIsoCommand.NotifyCanExecuteChanged();
        ApplyConsoleIsoCommand.NotifyCanExecuteChanged();
        DisconnectSelectedCommand.NotifyCanExecuteChanged();
        CancelConnectSelectedCommand.NotifyCanExecuteChanged();
        ReconnectSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        ImportExportVmCommand.NotifyCanExecuteChanged();
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
        RunAction(new VMStartAction(SelectedVm, ShellVmHaPrompt.WarningDialogHAInvalidConfig, ShellVmHaPrompt.StartDiagnosisForm));
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
        RunAction(new VMResumeAction(SelectedVm, ShellVmHaPrompt.WarningDialogHAInvalidConfig, ShellVmHaPrompt.StartDiagnosisForm));
    }

    [RelayCommand(CanExecute = nameof(CanPauseVm))]
    private void PauseVm()
    {
        if (SelectedVm == null)
            return;
        RunAction(new VMPause(SelectedVm));
    }

    [RelayCommand(CanExecute = nameof(CanUnpauseVm))]
    private void UnpauseVm()
    {
        if (SelectedVm == null)
            return;
        RunAction(new VMUnPause(SelectedVm));
    }

    [RelayCommand(CanExecute = nameof(ShowSuspendToggle))]
    private void ToggleSuspendVm()
    {
        if (CanResumeVm)
            ResumeVm();
        else if (CanSuspendVm)
            SuspendVm();
    }

    [RelayCommand(CanExecute = nameof(ShowPauseToggle))]
    private void TogglePauseVm()
    {
        if (CanUnpauseVm)
            UnpauseVm();
        else if (CanPauseVm)
            PauseVm();
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

    [RelayCommand]
    private async Task AddServerAsync()
    {
        var owner = GetMainWindow();
        var dialog = new AddServerWindow(this);
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    [RelayCommand(CanExecute = nameof(CanImportExportVm))]
    private async Task ImportExportVmAsync()
    {
        var conn = SelectedConnection;
        if (conn is not { IsConnected: true })
        {
            StatusMessage = "Connect to a server before importing or exporting.";
            return;
        }

        var owner = GetMainWindow();
        var choice = new ImportExportChoiceWindow();
        if (owner != null)
            await choice.ShowDialog(owner);
        else
        {
            choice.Show();
            return;
        }

        switch (choice.ResultChoice)
        {
            case ImportExportChoice.Import:
                await ShowImportDialogAsync(conn, owner);
                break;
            case ImportExportChoice.Export:
                await ShowExportDialogAsync(conn, owner);
                break;
            case ImportExportChoice.ImportOvf:
                await ShowOvfImportDialogAsync(conn, owner);
                break;
            case ImportExportChoice.ExportOvf:
                await ShowOvfExportDialogAsync(conn, owner);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportVm))]
    private async Task ExportVmAsync()
    {
        var conn = SelectedConnection;
        if (conn is not { IsConnected: true })
            return;
        await ShowExportDialogAsync(conn, GetMainWindow());
    }

    private async Task ShowExportDialogAsync(IXenConnection conn, Window? owner)
    {
        var dialog = new VmExportWindow(conn, SelectedVm, msg =>
        {
            ActionStatusMessage = msg;
            StatusMessage = msg;
        });
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    private async Task ShowImportDialogAsync(IXenConnection conn, Window? owner)
    {
        var dialog = new VmImportWindow(conn, ResolveSelectedHost(), msg =>
        {
            ActionStatusMessage = msg;
            StatusMessage = msg;
        });
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    private async Task ShowOvfImportDialogAsync(IXenConnection conn, Window? owner)
    {
        var dialog = new OvfImportWindow(conn, ResolveSelectedHost(), msg =>
        {
            ActionStatusMessage = msg;
            StatusMessage = msg;
        });
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    private async Task ShowOvfExportDialogAsync(IXenConnection conn, Window? owner)
    {
        var dialog = new OvfExportWindow(conn, SelectedVm, msg =>
        {
            ActionStatusMessage = msg;
            StatusMessage = msg;
        });
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    [RelayCommand(CanExecute = nameof(CanEditVm))]
    private async Task EditVmAsync()
    {
        if (SelectedVm == null)
            return;

        var owner = GetMainWindow();
        var dialog = new VmPropertiesWindow(SelectedVm, msg =>
        {
            ActionStatusMessage = msg;
            StatusMessage = msg;
        });
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    [RelayCommand(CanExecute = nameof(CanCloneVm))]
    private async Task CloneVmAsync()
    {
        if (SelectedVm == null)
            return;

        var owner = GetMainWindow();
        var dialog = new VmCloneWindow(SelectedVm, msg =>
        {
            ActionStatusMessage = msg;
            StatusMessage = msg;
        });
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    [RelayCommand(CanExecute = nameof(CanCopyVm))]
    private async Task CopyVmAsync()
    {
        if (SelectedVm == null)
            return;

        var owner = GetMainWindow();
        var dialog = new VmCopyWindow(SelectedVm, msg =>
        {
            ActionStatusMessage = msg;
            StatusMessage = msg;
        });
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    [RelayCommand(CanExecute = nameof(CanMigrateVm))]
    private async Task MigrateVmAsync()
    {
        if (SelectedVm == null)
            return;

        var owner = GetMainWindow();
        var dialog = new VmMigrateWindow(SelectedVm, msg =>
        {
            ActionStatusMessage = msg;
            StatusMessage = msg;
        });
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    [RelayCommand(CanExecute = nameof(CanCrossPoolMigrateVm))]
    private async Task CrossPoolMigrateVmAsync()
    {
        if (SelectedVm == null)
            return;

        var owner = GetMainWindow();
        var dialog = new VmCrossPoolMigrateWindow(SelectedVm, msg =>
        {
            ActionStatusMessage = msg;
            StatusMessage = msg;
        });
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    [RelayCommand(CanExecute = nameof(CanMoveVm))]
    private async Task MoveVmAsync()
    {
        if (SelectedVm == null)
            return;

        var owner = GetMainWindow();
        // WinForms MoveVMCommand: prefer migrate_send Move wizard when available; else VDI copy Move.
        // Prefer only when eligible hosts exist so single-host / restricted pools use VmMoveWindow.
        Window dialog = CanPreferMigrateSendMove(SelectedVm)
            ? new VmCrossPoolMigrateWindow(SelectedVm, msg =>
            {
                ActionStatusMessage = msg;
                StatusMessage = msg;
            }, ShellMigrateWizardMode.Move)
            : new VmMoveWindow(SelectedVm, msg =>
            {
                ActionStatusMessage = msg;
                StatusMessage = msg;
            });

        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteVm))]
    private async Task DeleteVmAsync()
    {
        if (SelectedVm == null)
            return;

        var owner = GetMainWindow();
        var dialog = new VmDeleteWindow(SelectedVm, msg =>
        {
            ActionStatusMessage = msg;
            StatusMessage = msg;
        });
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
        RefreshConsoleIsoOptions();
    }

    [RelayCommand(CanExecute = nameof(CanEjectIso))]
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
        AttachedIsoLabel = "Empty — no ISO inserted";
        OnPropertyChanged(nameof(CanEjectIso));
        OnPropertyChanged(nameof(CanApplyConsoleIso));
        EjectIsoCommand.NotifyCanExecuteChanged();
        ApplyConsoleIsoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApplyConsoleIso))]
    private void ApplyConsoleIso()
    {
        if (SelectedVm == null || SelectedConsoleIso == null || _suppressConsoleIsoSelection)
            return;

        ChangeVmIso(SelectedVm, SelectedConsoleIso.Vdi);
        AttachedIsoLabel = SelectedConsoleIso.Name;
        OnPropertyChanged(nameof(CanEjectIso));
        OnPropertyChanged(nameof(CanApplyConsoleIso));
        EjectIsoCommand.NotifyCanExecuteChanged();
        ApplyConsoleIsoCommand.NotifyCanExecuteChanged();
    }

    private void ChangeVmIso(VM vm, VDI? vdi)
    {
        var cdrom = vm.FindVMCDROM();
        if (cdrom == null)
        {
            var create = new CreateCdDriveAction(vm);
            create.Completed += a =>
            {
                if (!a.Succeeded)
                    return;
                var refreshed = vm.Connection.Resolve(new XenRef<VM>(vm.opaque_ref)) ?? vm;
                var drive = refreshed.FindVMCDROM();
                if (drive != null)
                    ShellActionRunner.Run(new ChangeVMISOAction(refreshed.Connection, refreshed, vdi, drive));
                Dispatcher.UIThread.Post(RefreshConsoleIsoOptions);
            };
            RunAction(create);
            return;
        }

        RunAction(new ChangeVMISOAction(vm.Connection, vm, vdi, cdrom));
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

        // Cap concurrency so large pools do not stampede the XenAPI task queue.
        var actions = srs.Select(sr => (AsyncAction)new SrRefreshAction(sr)).ToList();
        RunAction(new ParallelAction(
            "Refresh storage",
            "Refreshing storage…",
            "Storage refreshed.",
            actions,
            conn,
            maxNumberOfParallelActions: 4));
    }

    [RelayCommand]
    private void PopOutConsole()
    {
        if (!HasConsoleFrame || IsConsolePoppedOut)
            return;

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

        // Ownerless Show() so focusing the pop-out does not raise MainWindow
        // (WinForms undock used the same pattern). Lifetime is still tied via CloseConsolePopOut.
        window.Show();
        window.Activate();
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

    [RelayCommand]
    private void TakeSnapshot()
    {
        if (SelectedVm == null || !CanManageSnapshots)
            return;

        var name = string.IsNullOrWhiteSpace(NewSnapshotName)
            ? $"{SelectedVm.name_label} snapshot {DateTime.Now:yyyy-MM-dd HH:mm}"
            : NewSnapshotName.Trim();

        RunAction(new VMSnapshotCreateAction(
            SelectedVm,
            name,
            NewSnapshotDescription?.Trim() ?? string.Empty,
            SnapshotType.DISK,
            (_, _, _) => null!));

        NewSnapshotName = string.Empty;
        NewSnapshotDescription = string.Empty;
        SnapshotStatusMessage = "Snapshot queued — see Logs.";
    }

    [RelayCommand]
    private void RevertSnapshot(SnapshotItemRow? row)
    {
        if (row == null || SelectedVm?.Connection is not { IsConnected: true } conn)
            return;

        var snapshot = conn.Resolve(new XenRef<VM>(row.OpaqueRef));
        if (snapshot == null)
        {
            SnapshotStatusMessage = "Snapshot no longer available.";
            return;
        }

        RunAction(new VMSnapshotRevertAction(snapshot));
        SnapshotStatusMessage = "Revert queued — see Logs.";
    }

    [RelayCommand]
    private void DeleteSnapshot(SnapshotItemRow? row)
    {
        if (row == null || SelectedVm?.Connection is not { IsConnected: true } conn)
            return;

        var snapshot = conn.Resolve(new XenRef<VM>(row.OpaqueRef));
        if (snapshot == null)
        {
            SnapshotStatusMessage = "Snapshot no longer available.";
            return;
        }

        RunAction(new VMSnapshotDeleteAction(snapshot));
        SnapshotStatusMessage = "Delete queued — see Logs.";
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
