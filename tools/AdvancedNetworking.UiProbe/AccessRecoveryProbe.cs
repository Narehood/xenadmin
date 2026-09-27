using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation.Peers;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using XcpNgCenter.Shell.Views;

sealed partial class ProbeApp
{
    readonly Dictionary<Window, Size> requestedEditorSizes = new();

    void CheckAccessRecovery(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        DispatcherTimer.RunOnce(async () =>
        {
            try
            {
                await CheckAdWindows(lifetime);
                await CheckDrWindow(lifetime);
                File.WriteAllLines(Path.Combine(Evidence, "results.log"), checks.Prepend(
                    "PASS: production AD/DR windows with synthetic backend delegates; no profile, directory, pool, or credentials used."));
                Console.WriteLine($"Passed {checks.Count} access/recovery editor checks. Evidence: {Evidence}");
                theme?.Dispose(); settings?.Dispose(); lifetime.Shutdown(0);
            }
            catch (Exception error) { Fail(lifetime, error); }
        }, TimeSpan.FromMilliseconds(100));
    }

    void OpenProbeWindow(Window window, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        var requested = new Size(window.Width, window.Height);
        Require(window.SizeToContent == SizeToContent.Manual
            && double.IsFinite(requested.Width) && double.IsFinite(requested.Height)
            && requested.Width >= window.MinWidth && requested.Width <= window.MaxWidth
            && requested.Height >= window.MinHeight && requested.Height <= window.MaxHeight,
            $"{window.GetType().Name} declares valid manual default dimensions");
        requestedEditorSizes.Add(window, requested);
        // Win32 otherwise retains the desktop-sized native tracking ceiling.
        // Finite probe-only maxima let these hidden windows reach their XAML
        // defaults even on a smaller CI desktop, without changing that desktop.
        if (double.IsPositiveInfinity(window.MaxWidth)) window.MaxWidth = requested.Width;
        if (double.IsPositiveInfinity(window.MaxHeight)) window.MaxHeight = requested.Height;
        lifetime.MainWindow = window;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(-10000, -10000);
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        window.UpdateLayout();
    }

    async Task CheckEditorLayout(Window window, string scenario, params string[] footerNames)
    {
        var requested = requestedEditorSizes[window];
        foreach (var size in new[] { (requested.Width, requested.Height, "default"), (window.MinWidth, window.MinHeight, "minimum") })
        {
            window.Width = size.Item1; window.Height = size.Item2;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();
            var tolerance = 1d / window.RenderScaling;
            Require(Math.Abs(window.ClientSize.Width - size.Item1) <= tolerance
                && Math.Abs(window.ClientSize.Height - size.Item2) <= tolerance
                && Math.Abs(window.Bounds.Width - size.Item1) <= tolerance
                && Math.Abs(window.Bounds.Height - size.Item2) <= tolerance,
                $"{window.GetType().Name} {size.Item3} uses requested {size.Item1}x{size.Item2} logical pixels"
                + $" (client={window.ClientSize}, bounds={window.Bounds.Size}, native scale={window.RenderScaling})");
            foreach (var name in footerNames)
            {
                var button = window.FindControl<Button>(name) ?? throw new InvalidOperationException($"Missing footer control {name}");
                var point = button.TranslatePoint(new Point(), window)!.Value;
                Require(button.IsEffectivelyVisible && button.Bounds.Width > 0 && button.Bounds.Height > 0
                    && point.X >= 0 && point.Y >= 0 && point.X + button.Bounds.Width <= window.ClientSize.Width + 1
                    && point.Y + button.Bounds.Height <= window.ClientSize.Height + 1,
                    $"{window.GetType().Name} {name} remains visible at {size.Item3} size");
            }
            foreach (var scale in new[] { 1d, 1.5d, 2d }) Render(window, scenario + "-" + size.Item3, scale);
        }
        window.Width = requested.Width; window.Height = requested.Height; window.UpdateLayout();
    }

    void ClickCommandButton(Button button, ICommand command)
    {
        Require(ReferenceEquals(button.Command, command) && button.IsEffectivelyEnabled,
            $"{button.Content} button is enabled and bound to the expected command");
        // The public automation peer invokes Button's normal click/command path.
        new ButtonAutomationPeer(button).Invoke();
    }

    Task ClickAsyncCommand(Window window, string name, IAsyncRelayCommand command)
    {
        var button = window.FindControl<Button>(name)
            ?? window.GetVisualDescendants().OfType<Button>().SingleOrDefault(item => item.Content?.ToString() == name)
            ?? throw new InvalidOperationException($"Missing command button {name}");
        ClickCommandButton(button, command);
        var execution = command.ExecutionTask;
        Require(execution != null, $"{name} click starts its command");
        return execution!;
    }

    void RequireClearedCredentials(AdEditorWindow window, AdEditorViewModel vm, string scenario) =>
        Require(vm.Username.Length == 0 && vm.Password.Length == 0
            && string.IsNullOrEmpty(window.FindControl<TextBox>("UsernameText")!.Text)
            && string.IsNullOrEmpty(window.FindControl<TextBox>("PasswordText")!.Text)
            && window.FindControl<TextBox>("PasswordText")!.PasswordChar != '\0',
            $"AD {scenario} clears the view model and both bound credential boxes while preserving password masking");

    async Task CheckAdWindows(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        var roles = new[] { new AdRoleOption("role-admin", "uuid-admin", "Pool administrator", "All administrative operations", true),
            new AdRoleOption("role-reader", "uuid-reader", "Read only", "Inspect inventory", true) };
        AdSnapshot Snapshot(bool joined) => new("pool", "pool-uuid", "Synthetic access pool", "host",
            [new("host", "host-uuid", "Synthetic host", "192.0.2.10", joined ? "AD" : "", joined ? "example.org" : "", false, false, true, false)],
            joined ? [new("subject", "subject-uuid", "S-1-synthetic", "EXAMPLE\\readers", true, "Read only", "role-reader")] : [],
            roles, true, []);
        var snapshot = Snapshot(false);
        var join = new AdEditorWindow();
        var confirmed = false;
        var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var vm = new AdEditorViewModel(snapshot,
            (request, _) => Task.FromResult(new AdReview(snapshot.Fingerprint, request.Fingerprint, null, "Synthetic reviewed domain join.")),
            (_, _, _) => { calls++; return apply.Task; }, _ => Task.FromResult(confirmed), join.Close);
        join.DataContext = vm; OpenProbeWindow(join, lifetime);
        await CheckEditorLayout(join, "join", "CloseButton", "ReviewButton", "ApplyButton");
        join.FindControl<TextBox>("DomainText")!.Text = "example.org";
        join.FindControl<TextBox>("UsernameText")!.Text = "synthetic-admin";
        join.FindControl<TextBox>("PasswordText")!.Text = "synthetic-fixture-only";
        join.FindControl<CheckBox>("RecoveryConfirmation")!.IsChecked = true;
        Require(vm.Domain == "example.org" && vm.Username == "synthetic-admin" && vm.Password.Length > 0 && vm.RecoveryConfirmed,
            "AD text and recovery-confirmation controls update the isolated draft");
        Require(join.FindControl<TextBox>("PasswordText")!.PasswordChar != '\0', "AD password input masks credentials");
        Require(!join.FindControl<Button>("ApplyButton")!.IsEffectivelyEnabled, "AD Apply is disabled before review");
        await ClickAsyncCommand(join, "ReviewButton", vm.ReviewCommand);
        Require(vm.CanApply && join.FindControl<Button>("ApplyButton")!.IsEffectivelyEnabled, "AD successful review enables the bound Apply button");
        await ClickAsyncCommand(join, "ApplyButton", vm.ApplyCommand);
        Require(calls == 0 && vm.Username.Length == 0 && vm.Password.Length == 0 && join.IsVisible
            && string.IsNullOrEmpty(join.FindControl<TextBox>("UsernameText")!.Text)
            && string.IsNullOrEmpty(join.FindControl<TextBox>("PasswordText")!.Text)
            && join.FindControl<TextBox>("PasswordText")!.PasswordChar != '\0',
            "Declining AD confirmation clears the bound credential boxes, preserves masking, and leaves the editor open without mutation");
        join.FindControl<TextBox>("UsernameText")!.Text = "synthetic-admin";
        join.FindControl<TextBox>("PasswordText")!.Text = "synthetic-fixture-only";
        confirmed = true;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(!vm.CanApply, "AD credential account reentry requires a new review");
        await ClickAsyncCommand(join, "ReviewButton", vm.ReviewCommand);
        Require(vm.CanApply, "AD re-review restores Apply after credentials reentry");
        var saving = ClickAsyncCommand(join, "ApplyButton", vm.ApplyCommand);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(vm.IsSaving && !join.FindControl<Button>("CloseButton")!.IsEffectivelyEnabled
            && !join.FindControl<TextBox>("DomainText")!.IsEffectivelyEnabled,
            $"AD saving disables close and draft editing (saving={vm.IsSaving}, busy={vm.IsBusy}, calls={calls}, close enabled={join.FindControl<Button>("CloseButton")!.IsEffectivelyEnabled}, domain enabled={join.FindControl<TextBox>("DomainText")!.IsEffectivelyEnabled}, status={vm.StatusMessage})");
        join.Close();
        Require(join.IsVisible && vm.Username.Length == 0 && vm.Password.Length == 0
            && string.IsNullOrEmpty(join.FindControl<TextBox>("UsernameText")!.Text)
            && string.IsNullOrEmpty(join.FindControl<TextBox>("PasswordText")!.Text)
            && join.FindControl<TextBox>("PasswordText")!.PasswordChar != '\0',
            "AD native close is rejected while saving and both bound credential boxes remain cleared and masked");
        apply.SetException(new InvalidOperationException("Synthetic unconfirmed operation"));
        await saving;
        RequireClearedCredentials(join, vm, "failed save");
        Require(join.IsVisible && !vm.CanApply && !vm.CanReview && vm.StatusMessage.Contains(AdManagement.RecoveryNotice, StringComparison.Ordinal),
            "AD unconfirmed mutation stays visible and requires reopening before another operation");
        ClickCommandButton(join.FindControl<Button>("CloseButton")!, vm.CloseCommand);
        Require(!join.IsVisible, "AD idle Close button closes the actual window");

        var closing = new AdEditorWindow();
        var closingVm = new AdEditorViewModel(snapshot,
            (_, _) => throw new InvalidOperationException("Close scenario must not review"),
            (_, _, _) => throw new InvalidOperationException("Close scenario must not mutate"),
            _ => Task.FromResult(false), closing.Close);
        closing.DataContext = closingVm; OpenProbeWindow(closing, lifetime);
        closing.FindControl<TextBox>("UsernameText")!.Text = "synthetic-admin";
        closing.FindControl<TextBox>("PasswordText")!.Text = "synthetic-fixture-only";
        ClickCommandButton(closing.FindControl<Button>("CloseButton")!, closingVm.CloseCommand);
        Require(!closing.IsVisible, "AD Close button closes an editor with unsubmitted credentials");
        RequireClearedCredentials(closing, closingVm, "idle close");

        var accessSnapshot = Snapshot(true);
        var access = new AdEditorWindow();
        var accessVm = new AdEditorViewModel(accessSnapshot,
            (request, _) => Task.FromResult(new AdReview(accessSnapshot.Fingerprint, request.Fingerprint, null, "Synthetic roles review.")),
            (_, _, _) => Task.CompletedTask, _ => Task.FromResult(false), access.Close);
        access.DataContext = accessVm; OpenProbeWindow(access, lifetime);
        access.FindControl<ComboBox>("OperationSelector")!.SelectedItem = accessVm.Operations.Single(operation => operation.Operation == AdOperation.SetRoles);
        access.FindControl<ListBox>("SubjectList")!.SelectedItem = accessVm.Subjects.Single();
        Require(accessVm.SelectedSubject?.Reference == "subject" && accessVm.Roles.Single(role => role.Role.Reference == "role-reader").IsSelected,
            "AD selecting an existing subject loads its role draft through production bindings");
        var admin = access.GetVisualDescendants().OfType<CheckBox>().Single(check => check.Content?.ToString() == "Pool administrator");
        admin.IsChecked = true;
        access.FindControl<CheckBox>("RecoveryConfirmation")!.IsChecked = true;
        Require(!access.FindControl<Button>("ApplyButton")!.IsEffectivelyEnabled, "AD role Apply is disabled before review");
        await ClickAsyncCommand(access, "ReviewButton", accessVm.ReviewCommand);
        Require(accessVm.CanApply && accessVm.Roles.Single(role => role.Role.Reference == "role-admin").IsSelected,
            "AD role checkbox participates in the reviewed request");
        admin.IsChecked = false;
        Require(!accessVm.CanApply, "AD changing a reviewed role invalidates Apply");
        await CheckEditorLayout(access, "roles", "CloseButton", "ReviewButton", "ApplyButton");
        ClickCommandButton(access.FindControl<Button>("CloseButton")!, accessVm.CloseCommand);
    }

    async Task CheckDrWindow(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        var backend = new SyntheticDrWorkflow();
        var window = new DrEditorWindow();
        var vm = new DrEditorViewModel(backend, _ => Task.FromResult(true), window.Close);
        window.DataContext = vm; OpenProbeWindow(window, lifetime);
        await CheckEditorLayout(window, "initial", "ReviewButton", "RecoverButton", "CleanupButton", "CloseButton");
        Require(vm.IsRehearsal && !window.FindControl<Button>("RecoverButton")!.IsEffectivelyEnabled,
            "DR starts in metadata rehearsal mode with recovery unavailable before inspection and review");
        await ClickAsyncCommand(window, "Discover metadata", vm.DiscoverCommand);
        window.FindControl<ComboBox>("MetadataSelector")!.SelectedItem = vm.Metadata.Single();
        Require(vm.CanInspect && vm.SelectedMetadata?.Uuid == "metadata-uuid", "DR metadata selector updates inspection availability");
        await ClickAsyncCommand(window, "Inspect metadata", vm.InspectCommand);
        window.UpdateLayout();
        var vmCheck = window.GetVisualDescendants().OfType<CheckBox>().Single(check => check.DataContext is DrVmDraft { IsAvailable: true });
        vmCheck.IsChecked = true;
        window.UpdateLayout();
        Require(vm.Vms.Single(row => row.IsAvailable).IsSelected && vm.Storage.Single().SelectedTarget?.Reference == "target-sr",
            "DR VM checkbox selects its reviewed storage UUID match");
        var blockedVm = window.GetVisualDescendants().OfType<CheckBox>().Single(check => check.DataContext is DrVmDraft { IsAvailable: false });
        Require(!blockedVm.IsEffectivelyEnabled, "DR unsupported VM cannot be selected through the actual control");
        var network = window.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.DataContext is DrNetworkDraft);
        Require(vm.Networks.Single().Targets.Count == 1 && vm.Networks.Single().Targets.Single().IsIsolated,
            "DR rehearsal offers only isolated target networks");
        network.SelectedItem = vm.Networks.Single().Targets.Single();
        Require(vm.Networks.Single().SelectedTarget?.Reference == "isolated-network", "DR network selector updates the mapping draft");
        Require(!window.FindControl<Button>("RecoverButton")!.IsEffectivelyEnabled, "DR Recover remains disabled until review");
        await ClickAsyncCommand(window, "ReviewButton", vm.ReviewCommand);
        Require(vm.CanRecover && window.FindControl<Button>("RecoverButton")!.IsEffectivelyEnabled, "DR server review enables the bound recovery control");
        window.FindControl<CheckBox>("RehearsalCheckbox")!.IsChecked = false;
        Require(!vm.IsRehearsal && !vm.CanRecover && vm.Networks.Single().Targets.Count == 2,
            "DR mode change invalidates approval and reveals regular recovery networks");
        window.FindControl<CheckBox>("RehearsalCheckbox")!.IsChecked = true;
        await ClickAsyncCommand(window, "ReviewButton", vm.ReviewCommand);
        var running = ClickAsyncCommand(window, "RecoverButton", vm.RecoverCommand);
        Require(vm.IsBusy && !window.FindControl<Button>("CloseButton")!.IsEffectivelyEnabled
            && !window.FindControl<ComboBox>("MetadataSelector")!.IsEffectivelyEnabled, "DR execution disables close and changing the reviewed metadata");
        window.Close();
        Require(window.IsVisible, "DR native close is rejected during recovery");
        backend.Recovery.SetResult(new DrOutcome(DrMode.MetadataRehearsal,
            [new("Synthetic VM", "vm-uuid", "Recovered halted; synthetic mappings applied.", true)],
            [new("recovered-vm", "vm-uuid", "Synthetic VM", "synthetic-receipt")]));
        await running;
        Require(vm.CanCleanup && vm.HasReport && !vm.CanRecover, "DR successful rehearsal retains its report and offers cleanup without retrying recovery");
        await ClickAsyncCommand(window, "CleanupButton", vm.CleanupCommand);
        Require(backend.CleanupCalls == 1 && vm.HasError && vm.Error.Contains("Synthetic cleanup interruption") && !vm.CanCleanup && window.IsVisible,
            "DR interrupted cleanup remains visible and cannot be automatically retried");
        await CheckEditorLayout(window, "result", "ReviewButton", "RecoverButton", "CleanupButton", "CloseButton");
        ClickCommandButton(window.FindControl<Button>("CloseButton")!, vm.CloseCommand);
        Require(!window.IsVisible, "DR idle Close button closes the actual window");
    }

    sealed class SyntheticDrWorkflow : IDrWorkflow
    {
        public string PoolName => "Synthetic recovery pool";
        public TaskCompletionSource<DrOutcome> Recovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CleanupCalls { get; private set; }
        static readonly DrMetadataOption Metadata = new("metadata", "metadata-uuid", "Synthetic metadata", "Replicated storage");
        public Task<IReadOnlyList<DrMetadataOption>> DiscoverAsync() => Task.FromResult<IReadOnlyList<DrMetadataOption>>([Metadata]);
        public Task<DrInspection> InspectAsync(DrMetadataOption metadata) => Task.FromResult(new DrInspection(metadata, "Source pool", "source-pool-uuid",
            [new("source-vm", "vm-uuid", "Synthetic VM", null, ["source-sr"], ["source-network"]),
                new("unsupported-vm", "unsupported-uuid", "Unsupported VM", "Synthetic hardware restriction", [], [])],
            [new("source-sr", "sr-uuid", "Replicated VM storage")], [new("source-network", "source-network-uuid", "Original VM network")],
            [new("target-sr", "sr-uuid", "Matching replica")],
            [new("isolated-network", "isolated-uuid", "Empty internal network", true), new("production-network", "production-uuid", "Physical network", false, true)]));
        public Task<DrReview> ReviewAsync(DrInspection inspection, DrRequest request) => Task.FromResult(new DrReview(request.Fingerprint, inspection.Fingerprint, "synthetic-target",
            "Synthetic recovery review: keep the VM halted, inspect the mapped network, and retain the source disks."));
        public Task<DrOutcome> RecoverAsync(DrInspection inspection, DrRequest request, DrReview review) => Recovery.Task;
        public Task<DrOutcome> CleanupAsync(DrOutcome outcome)
        {
            CleanupCalls++;
            return Task.FromResult(new DrOutcome(outcome.Mode, [], outcome.Cleanup, "Synthetic cleanup interruption. Inspect the remaining records before manual cleanup."));
        }
    }
}
