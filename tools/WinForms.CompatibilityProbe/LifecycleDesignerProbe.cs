using System.Collections;
using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;

// Exercise the built client's controls on an STA without starting the application,
// loading saved connections/settings, or running any server actions.
internal sealed class LifecycleDesignerProbe(Assembly assembly)
{
    private readonly List<string> failures = [];
    private int checks;
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static int Run(Assembly assembly)
    {
        var probe = new LifecycleDesignerProbe(assembly);
        var thread = new Thread(probe.Check);
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45)))
        {
            Console.Error.WriteLine("WinForms lifecycle/designer probe timed out.");
            // No foreground worker should keep a failed CI process alive.
            Environment.Exit(1);
        }
        foreach (var failure in probe.failures)
            Console.Error.WriteLine(failure);
        Console.WriteLine($"WinForms lifecycle/designer: {probe.checks} checks, {probe.failures.Count} failures.");
        return probe.failures.Count == 0 ? 0 : 1;
    }

    private void Check()
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        RunCase("Tree designer values", () => CheckDesignerValues("CustomTreeView",
            ("NodeIndent", 5), ("ShowCheckboxes", false), ("ShowDescription", false),
            ("ShowImages", true), ("ShowRootLines", false), ("RootAlwaysExpanded", true)));
        RunCase("Panel designer values", () => CheckDesignerValues("FlickerFreePanel",
            ("BorderWidth", 3), ("BorderColor", Color.Red)));
        RunCase("Pool/host picker designer values", () => CheckDesignerValues("PoolHostPicker",
            ("ShowCheckboxes", true), ("ShowImages", false)));
        foreach (var typeName in new[] { "MenuStripEx", "ToolStripEx", "StatusStripEx" })
            RunCase(typeName, () => CheckDesignerValues(typeName, ("ClickThrough", true)));
        RunCase("Storage picker defaults", CheckStoragePickerDefaults);
        RunCase("Password close cancellation", CheckPasswordClose);
        RunCase("HA close cancellation", CheckHaClose);
        foreach (var typeName in new[] { "InstallCertificateDialog", "ResolvingSubjectsDialog" })
            RunCase(typeName, () => CheckIdleActionDialogClose(typeName));
        RunCase("Folder registry cleanup", CheckFolderClose);
        RunCase("Connection registry cleanup", CheckConnectionClose);
        RunCase("Wizard close events", CheckWizardClose);
        RunCase("Owner focus shutdown guards", CheckOwnerFocus);
        RunCase("HA cleanup failure", CheckHaCleanupFailure);
        RunCase("Base cleanup failure", CheckBaseCleanupFailure);
        RunCase("Main window exit guards", CheckMainWindowGuards);
    }

    private void RunCase(string name, Action action)
    {
        try { action(); }
        catch (Exception error) { failures.Add($"{name}: {error}"); }
    }

    private void Require(bool condition, string description)
    {
        checks++;
        if (!condition) failures.Add(description);
    }

    private Type ClientType(string name) => assembly.GetType("XenAdmin." + name, throwOnError: true)!;

    private void CheckDesignerValues(string typeName, params (string Name, object Changed)[] values)
    {
        var type = ClientType("Controls." + typeName);
        using var control = (Control)Activator.CreateInstance(type)!;
        using var restored = (Control)Activator.CreateInstance(type)!;
        foreach (var (name, changed) in values)
        {
            var property = TypeDescriptor.GetProperties(control)[name]!;
            var original = property.GetValue(control);
            Require(!property.ShouldSerializeValue(control), $"{typeName}.{name}: initial value should be omitted.");
            property.SetValue(control, changed);
            Require(property.ShouldSerializeValue(control), $"{typeName}.{name}: edited value must be serialized.");
            Require(property.SerializationVisibility == DesignerSerializationVisibility.Visible,
                $"{typeName}.{name}: edited values must remain visible to the designer.");
            // Replay the value through the same descriptors used by the designer.
            property.SetValue(restored, property.GetValue(control));
            Require(Equals(property.GetValue(restored), changed), $"{typeName}.{name}: edited value did not survive replay.");
            property.ResetValue(control);
            Require(Equals(property.GetValue(control), original) && !property.ShouldSerializeValue(control),
                $"{typeName}.{name}: reset must restore the constructor default and omit it.");
        }
    }

    private void CheckStoragePickerDefaults()
    {
        using var picker = (Control)Activator.CreateInstance(ClientType("Controls.SrPicker"))!;
        foreach (var name in new[] { "NodeIndent", "ShowCheckboxes", "ShowDescription", "ShowImages" })
        {
            var property = TypeDescriptor.GetProperties(picker)[name]!;
            var defaultValue = (DefaultValueAttribute?)property.Attributes[typeof(DefaultValueAttribute)];
            Require(defaultValue != null && Equals(defaultValue.Value, property.GetValue(picker)),
                $"SrPicker.{name}: inherited defaults must match this control's override.");
        }
    }

    private void CheckPasswordClose()
    {
        var type = ClientType("Dialogs.ChangeServerPasswordDialog");
        var hostType = type.GetConstructors().Single(c => c.GetParameters()[0].ParameterType.Name == "Host")
            .GetParameters()[0].ParameterType;
        var host = Activator.CreateInstance(hostType)!;
        using var dialog = (Form)Activator.CreateInstance(type, host)!;
        var eventField = FindField(hostType, "PropertyChanged");
        bool Subscribed() => ((Delegate?)eventField.GetValue(host))?.GetInvocationList().Any(d => d.Target == dialog) == true;
        Require(Subscribed(), "Password dialog must start subscribed to host updates.");
        // Create the real HWND so Close uses the window-message path, without OnLoad
        // (which needs a populated server cache). No server or profile is consulted.
        _ = dialog.Handle;
        FormClosingEventHandler cancel = (_, e) => e.Cancel = true;
        dialog.FormClosing += cancel;
        dialog.Close();
        Require(!dialog.IsDisposed && Subscribed(), "Cancelled password close must retain its host subscription.");
        dialog.FormClosing -= cancel;
        var closed = 0;
        dialog.FormClosed += (_, _) =>
        {
            closed++;
            Require(!Subscribed(), "Password listener must be detached before FormClosed observers run.");
        };
        dialog.Close();
        Require(dialog.IsDisposed && closed == 1 && !Subscribed(), "Accepted password close must clean up exactly once.");
    }

    private void CheckFolderClose()
    {
        using var dialog = (Form)Activator.CreateInstance(ClientType("Dialogs.FolderChangeDialog"), new object?[] { null })!;
        var baseType = ClientType("Dialogs.XenDialogBase");
        var modelType = baseType.GetMethod("ShowPerXenObject")!.GetParameters()[0].ParameterType.Assembly.GetType("XenAPI.VM")!;
        var owner = Activator.CreateInstance(modelType)!;
        modelType.GetProperty("opaque_ref")!.SetValue(owner, "OpaqueRef:lifecycle-probe");
        var registry = (IDictionary)baseType.GetField("instancePerXenObject", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        // Seed the registry used by ShowPerXenObject, avoiding its Show/OnLoad inventory scan.
        baseType.GetField("ownerXenObject", InstanceMembers)!.SetValue(dialog, owner);
        registry.Add(owner, dialog);
        try
        {
            _ = dialog.Handle;
            FormClosingEventHandler cancel = (_, e) => e.Cancel = true;
            dialog.FormClosing += cancel;
            dialog.Close();
            Require(registry.Contains(owner) && !dialog.IsDisposed, "Cancelled folder close must keep its owner registration.");
            dialog.FormClosing -= cancel;
            var closed = 0;
            dialog.FormClosed += (_, _) =>
            {
                closed++;
                Require(!registry.Contains(owner), "Folder close must call base cleanup before notifying FormClosed.");
            };
            dialog.Close();
            Require(dialog.IsDisposed && closed == 1 && !registry.Contains(owner), "Folder close must release its owner registration.");
        }
        finally { registry.Remove(owner); }
    }

    private void CheckHaClose()
    {
        var type = ClientType("Dialogs.EditVmHaPrioritiesDialog");
        var poolType = type.GetConstructors().Single().GetParameters()[0].ParameterType;
        var pool = Activator.CreateInstance(poolType)!;
        var connectionType = poolType.Assembly.GetType("XenAdmin.Network.XenConnection")!;
        poolType.GetProperty("Connection")!.SetValue(pool, Activator.CreateInstance(connectionType));
        poolType.GetProperty("ha_enabled")!.SetValue(pool, true);
        using var dialog = (Form)Activator.CreateInstance(type, pool)!;
        var priorities = type.GetField("assignPriorities", InstanceMembers)!.GetValue(dialog)!;
        bool Subscribed(object publisher, string name) => ((Delegate?)FindField(publisher.GetType(), name).GetValue(publisher))?
            .GetInvocationList().Any(d => d.Target == dialog) == true;
        Require(Subscribed(pool, "PropertyChanged") && Subscribed(priorities, "StatusChanged"), "HA dialog must start with both listeners.");
        _ = dialog.Handle;
        FormClosingEventHandler cancel = (_, e) => e.Cancel = true;
        dialog.FormClosing += cancel;
        dialog.Close();
        Require(!dialog.IsDisposed && Subscribed(pool, "PropertyChanged") && Subscribed(priorities, "StatusChanged"),
            "Cancelled HA close must keep pool and priority listeners.");
        dialog.FormClosing -= cancel;
        var closed = 0;
        dialog.FormClosed += (_, _) =>
        {
            closed++;
            Require(!Subscribed(pool, "PropertyChanged") && !Subscribed(priorities, "StatusChanged"),
                "Accepted HA close must detach both listeners before notification.");
        };
        dialog.Close();
        Require(dialog.IsDisposed && closed == 1, "HA dialog must close exactly once.");
    }

    private void CheckIdleActionDialogClose(string typeName)
    {
        // Parameterless constructors leave their action fields null. Active server
        // cancellation remains a live acceptance case, not a task for this probe.
        using var dialog = (Form)Activator.CreateInstance(ClientType("Dialogs." + typeName), nonPublic: true)!;
        _ = dialog.Handle;
        var closed = 0;
        dialog.FormClosed += (_, _) => closed++;
        FormClosingEventHandler cancel = (_, e) => e.Cancel = true;
        dialog.FormClosing += cancel;
        dialog.Close();
        Require(!dialog.IsDisposed && closed == 0, $"{typeName}: cancellation must keep the dialog open.");
        dialog.FormClosing -= cancel;
        dialog.Close();
        Require(dialog.IsDisposed && closed == 1, $"{typeName}: closing an idle dialog must notify once.");
    }

    private void CheckConnectionClose()
    {
        var type = ClientType("Dialogs.XenDialogBase");
        var constructor = type.GetConstructors(InstanceMembers).Single(c => c.GetParameters().Length == 1);
        var connectionType = constructor.GetParameters()[0].ParameterType.Assembly.GetType("XenAdmin.Network.XenConnection")!;
        var connection = Activator.CreateInstance(connectionType)!;
        using var dialog = (Form)constructor.Invoke([connection]);
        var registry = (IDictionary)type.GetField("instances", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var dialogs = (IList)registry[connection]!;
        try
        {
            Require(dialogs.Contains(dialog), "Connection dialog must be registered during construction.");
            _ = dialog.Handle;
            dialog.FormClosed += (_, _) => Require(!dialogs.Contains(dialog), "Base cleanup must precede FormClosed observers.");
            dialog.Close();
            Require(dialog.IsDisposed && !dialogs.Contains(dialog), "Connection dialog must be removed after close.");
        }
        finally { registry.Remove(connection); }
    }

    private void CheckWizardClose()
    {
        var type = ClientType("Wizards.XenWizardBase");
        using var wizard = (Form)Activator.CreateInstance(type, nonPublic: true)!;
        // A completed wizard requires no live page/action and must still raise close events.
        type.GetField("wizardFinished", InstanceMembers)!.SetValue(wizard, true);
        _ = wizard.Handle;
        var closed = 0;
        wizard.FormClosed += (_, _) => closed++;
        FormClosingEventHandler cancel = (_, e) => e.Cancel = true;
        wizard.FormClosing += cancel;
        wizard.Close();
        Require(!wizard.IsDisposed && closed == 0, "Cancelled wizard close must not raise FormClosed.");
        wizard.FormClosing -= cancel;
        wizard.Close();
        Require(wizard.IsDisposed && closed == 1, "Wizard must deliver FormClosed exactly once.");
    }

    private void CheckMainWindowGuards()
    {
        var type = ClientType("MainWindow");
        // Deliberately omit MainWindow's profile/inventory constructor. Only exercise
        // the early exit paths, which must not touch controls, settings or actions.
        var main = (Form)RuntimeHelpers.GetUninitializedObject(type);
        GC.SuppressFinalize(main);
        var closing = type.GetMethod("OnFormClosing", InstanceMembers)!;
        var manager = Assembly.Load("XenModel").GetType("XenAdmin.ConnectionsManager", throwOnError: true)!;
        var history = (IList)manager.GetField("History")!.GetValue(null)!;
        if (history.Count != 0)
            throw new InvalidOperationException("The isolated exit probe requires an empty action history.");
        // A poison entry makes any fall-through into the task scan throw before
        // settings access. No real action, window constructor or test hook is used.
        // IList uses List<T>'s implementation without ChangeableList notifications.
        history.Add(null);
        var notifications = 0;
        FormClosingEventHandler listener = (_, _) => notifications++;
        FormClosingEventHandler cancel = (_, e) => e.Cancel = true;
        try
        {
            main.FormClosing += listener;
            var exit = new FormClosingEventArgs(CloseReason.ApplicationExitCall, false);
            closing.Invoke(main, [exit]);
            Require(!exit.Cancel && notifications == 1, "Final Application.Exit must notify listeners without entering the task scan.");
            main.FormClosing += cancel;
            foreach (var reason in new[] { CloseReason.UserClosing, CloseReason.WindowsShutDown, CloseReason.ApplicationExitCall })
            {
                var cancelled = new FormClosingEventArgs(reason, false);
                closing.Invoke(main, [cancelled]);
                Require(cancelled.Cancel, $"Main window must respect listener cancellation for {reason}.");
            }
        }
        finally
        {
            history.Clear();
            main.FormClosing -= cancel;
            main.FormClosing -= listener;
        }
    }

    private void CheckOwnerFocus()
    {
        var restore = ClientType("Core.FormCloseHelper").GetMethod("RestoreOwnerFocus", BindingFlags.Static | BindingFlags.NonPublic)!;
        void Restore(Form? owner, CloseReason reason, Action<Form> action) => restore.Invoke(null, [owner, reason, action]);
        using var owner = new Form();
        var attempts = 0;
        foreach (var reason in new[] { CloseReason.ApplicationExitCall, CloseReason.FormOwnerClosing, CloseReason.WindowsShutDown })
        {
            Restore(owner, reason, _ => attempts++);
            Require(attempts == 0, $"Owner focus must be skipped for {reason}.");
        }
        Restore(null, CloseReason.UserClosing, _ => attempts++);
        Require(attempts == 0, "A null owner must not receive focus.");
        Restore(owner, CloseReason.UserClosing, target =>
        {
            Require(ReferenceEquals(owner, target), "Normal focus must target the owner.");
            attempts++;
        });
        Require(attempts == 1, "A normal close must restore owner focus once.");
        Restore(owner, CloseReason.UserClosing, _ => throw new ObjectDisposedException("reentrant owner disposal"));
        Require(true, "Owner disposal during focus must not escape the close path.");
        owner.Dispose();
        Restore(owner, CloseReason.UserClosing, _ => attempts++);
        Require(attempts == 1, "An already disposed owner must not receive focus.");
        using var disposing = new DisposingOwnerForm(form =>
        {
            Require(form.Disposing, "The disposing-owner fixture must run during disposal.");
            Restore(form, CloseReason.UserClosing, _ => attempts++);
        });
        _ = disposing.Handle;
        disposing.Dispose();
        Require(disposing.Checked && attempts == 1, "A disposing owner must not receive focus.");
    }

    private void CheckHaCleanupFailure()
    {
        var type = ClientType("Dialogs.EditVmHaPrioritiesDialog");
        var poolType = type.GetConstructors().Single().GetParameters()[0].ParameterType;
        var pool = Activator.CreateInstance(poolType)!;
        poolType.GetProperty("Connection")!.SetValue(pool,
            Activator.CreateInstance(poolType.Assembly.GetType("XenAdmin.Network.XenConnection")!));
        poolType.GetProperty("ha_enabled")!.SetValue(pool, true);
        using var dialog = (Form)Activator.CreateInstance(type, pool)!;
        var priorities = type.GetField("assignPriorities", InstanceMembers)!.GetValue(dialog)!;
        var indicator = FindField(priorities.GetType(), "haNtolIndicator").GetValue(priorities)!;
        var workerField = FindField(indicator.GetType(), "ntolUpdateThread");
        var wait = (WaitHandle)FindField(indicator.GetType(), "waitingNtolUpdate").GetValue(indicator)!;
        // Simulate a disposed wait handle during teardown. Never start a worker or RPC.
        workerField.SetValue(indicator, new Thread(() => { }));
        wait.Dispose();
        RegisterOpenForm(dialog);
        var notifications = 0;
        dialog.FormClosed += (_, _) => notifications++;
        try
        {
            try
            {
                type.GetMethod("OnFormClosed", InstanceMembers)!.Invoke(dialog, [new FormClosedEventArgs(CloseReason.ApplicationExitCall)]);
                Require(false, "HA cleanup fixture must exercise the disposed wait handle.");
            }
            catch (TargetInvocationException error) when (error.InnerException is ObjectDisposedException)
            {
                Require(true, "Unexpected cleanup failures must remain observable.");
            }
            Require(notifications == 1 && !Application.OpenForms.Cast<Form>().Contains(dialog),
                "HA cleanup failure must still reach FormClosed and leave OpenForms.");
            var handlers = (Delegate?)FindField(priorities.GetType(), "StatusChanged").GetValue(priorities);
            Require(handlers?.GetInvocationList().Any(d => d.Target == dialog) != true,
                "HA listener must detach before stopping the failed worker.");
        }
        finally { workerField.SetValue(indicator, null); }
    }

    private void CheckBaseCleanupFailure()
    {
        var type = ClientType("Dialogs.XenDialogBase");
        var constructor = type.GetConstructors(InstanceMembers).Single(c => c.GetParameters().Length == 1);
        var connectionType = constructor.GetParameters()[0].ParameterType.Assembly.GetType("XenAdmin.Network.XenConnection")!;
        var connection = Activator.CreateInstance(connectionType)!;
        using var dialog = (Form)constructor.Invoke([connection]);
        var registry = (IDictionary)type.GetField("instances", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        // Inject a failing registry operation, without a live connection or profile.
        registry[connection] = null;
        RegisterOpenForm(dialog);
        var notifications = 0;
        dialog.FormClosed += (_, _) => notifications++;
        try
        {
            try
            {
                type.GetMethod("OnFormClosed", InstanceMembers)!.Invoke(dialog, [new FormClosedEventArgs(CloseReason.ApplicationExitCall)]);
                Require(false, "Base cleanup fixture must exercise a registry failure.");
            }
            catch (TargetInvocationException error) when (error.InnerException is NullReferenceException)
            {
                Require(true, "Registry failure must remain observable.");
            }
            Require(notifications == 1 && !Application.OpenForms.Cast<Form>().Contains(dialog),
                "Registry failure must still reach FormClosed and leave OpenForms.");
        }
        finally { registry.Remove(connection); }
    }

    private void RegisterOpenForm(Form form)
    {
        // Seed the same collection as Form.OnLoad without showing inventory dialogs.
        typeof(FormCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(Form)])!.Invoke(Application.OpenForms, [form]);
        Require(Application.OpenForms.Cast<Form>().Contains(form), "Cleanup fixture must start registered in OpenForms.");
    }

    private sealed class DisposingOwnerForm(Action<Form> check) : Form
    {
        public bool Checked { get; private set; }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Checked = true;
            check(this);
            base.OnHandleDestroyed(e);
        }
    }

    private static FieldInfo FindField(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
            if (current.GetField(name, InstanceMembers | BindingFlags.DeclaredOnly) is { } field)
                return field;
        throw new MissingFieldException(type.FullName, name);
    }
}
