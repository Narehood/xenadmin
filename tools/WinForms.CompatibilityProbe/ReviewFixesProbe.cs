using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;

// Exercises the built WinForms implementation with disposable synthetic profiles.
internal static class ReviewFixesProbe
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    /// <summary>Runs built-client plugin and credential regressions using only disposable synthetic files and profiles.</summary>
    /// <param name="assembly">The built WinForms client assembly under test.</param>
    /// <returns>Zero when all isolated regression probes pass.</returns>
    public static int Run(Assembly assembly)
    {
        string root = Path.Combine(Path.GetTempPath(), "winforms-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Plugin(assembly, root);
            Credentials(assembly, root);
            Console.WriteLine("Review fixes: PowerShell literal execution and credential migration/atomic-save probes passed.");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Checks all five PowerShell quote delimiters, bulk payload transport, and cleanup after execution or launch failure.</summary>
    /// <param name="assembly">The built WinForms client assembly under test.</param>
    /// <param name="root">The disposable probe directory.</param>
    private static void Plugin(Assembly assembly, string root)
    {
        var type = assembly.GetType("XenAdmin.Plugins.XenServerPowershellCmd", true)!;
        var vmType = assembly.GetType("XenAPI.VM", false) ?? assembly.GetReferencedAssemblies()
            .Select(Assembly.Load).Select(a => a.GetType("XenAPI.VM", false)).First(t => t != null)!;
        var vm = Activator.CreateInstance(vmType)!;
        var method = type.GetMethod("MakeInvocationExpression", Static)!;
        var targetType = method.GetParameters()[4].ParameterType.GetGenericArguments()[0];
        var targets = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(targetType))!;
        targets.Add(vm);
        string script = Path.Combine(root, "plugin '[literal] $(21+21).ps1");
        File.WriteAllText(script, "if ([IO.File]::Exists($env:XC_PROBE_PAYLOAD)) { throw 'Payload was not removed before execution' }; function ReviewRun { [Console]::WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ParamArray[0]))); [Console]::WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ObjInfoArray[0].url))) }");
        var factory = type.GetMethod("CreateCommandProcess", Static)!;
        string hostile = "$(21+21) ' ` \" ; C:\\folder\\file\r\nUnicode \u2018quoted\u2019 \u2603 \u201A; [Console]::WriteLine('INJECTED'); # \u201B; [Console]::WriteLine('INJECTED'); #";
        foreach (bool bulk in new[] { false, true })
        {
            string value = hostile + (bulk ? new string('x', 500) : "");
            vmType.GetProperty("name_label")!.SetValue(vm, value);
            string[] parameters = Enumerable.Range(0, bulk ? 200 : 1).SelectMany(_ => new[] { value, "token", "VM", "uuid" }).ToArray();
            string command = (string)method.Invoke(null, new object[] { script, "ReviewRun", parameters, new[] { "{$label}" }, targets, false })!;
            using var process = (Process)factory.Invoke(null, new object[] { command, false })!;
            string payload = (string)process.GetType().GetField("payloadPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(process)!;
            Check(File.Exists(payload), "Plugin source was not staged.");
            Check(process.StartInfo.Arguments.Length < 4000, "Plugin arguments grew with target data.");
            if (bulk) Check(command.Length > 32768, "Bulk fixture did not exceed the old command-line limit.");
            process.StartInfo.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            process.StartInfo.Arguments = "-NoProfile -NonInteractive " + process.StartInfo.Arguments;
            process.StartInfo.Environment["XC_PROBE_PAYLOAD"] = payload;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000)) { process.Kill(true); throw new TimeoutException("PowerShell probe timed out."); }
            string expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            var lines = output.GetAwaiter().GetResult().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Check(process.ExitCode == 0 && lines.SequenceEqual(new[] { expected, expected }), "PowerShell values were not preserved: " + errors.GetAwaiter().GetResult());
            Check(!File.Exists(payload), "Child left its plugin payload behind.");
        }
        string failedPayload;
        using (var failed = (Process)factory.Invoke(null, new object[] { "42;", false })!)
        {
            failedPayload = (string)failed.GetType().GetField("payloadPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(failed)!;
            failed.StartInfo.FileName = Path.Combine(root, "missing-powershell.exe");
            try { failed.Start(); throw new InvalidOperationException("Expected launch failure."); }
            catch (System.ComponentModel.Win32Exception) { }
        }
        Check(!File.Exists(failedPayload), "Failed launch left its plugin payload behind.");
    }

    /// <summary>Exercises real legacy migration, versioned unlock and credential writes, including invalid batches, cancellation and failed atomic saves.</summary>
    /// <param name="assembly">The built WinForms client assembly under test.</param>
    /// <param name="root">The disposable probe directory.</param>
    private static void Credentials(Assembly assembly, string root)
    {
        var properties = assembly.GetType("XenAdmin.Properties.Settings", true)!;
        var settings = assembly.GetType("XenAdmin.Settings", true)!;
        var program = assembly.GetType("XenAdmin.Program", true)!;
        var library = Assembly.Load(assembly.GetReferencedAssemblies().Single(n => n.Name == "XenCenterLib"));
        var encryption = library.GetType("XenCenterLib.EncryptionUtils", true)!;
        properties.GetProperty("SettingsPath", Static)!.SetValue(null, root);
        object profile = Activator.CreateInstance(properties)!;
        properties.GetProperty("Default", Static)!.SetValue(null, profile);
        properties.GetProperty("RequirePass")!.SetValue(profile, true);
        properties.GetProperty("SaveSession")!.SetValue(profile, true);
        const string password = "synthetic main password";
        var hash = encryption.GetMethod("ComputeHash", Static)!.Invoke(null, new object[] { password, Enum.ToObject(encryption.GetNestedType("HashMethod")!, 1) });
        string clear = "user\u202fsynthetic.invalid\u202f443\u202fsecret\u202fdisconnected\u202fFriendly\u202f";
        string legacy = (string)encryption.GetMethod("EncryptString", Static)!.Invoke(null, new[] { clear, hash })!;
        properties.GetProperty("ServerList")!.SetValue(profile, new[] { legacy });
        // The private path is the exact path selected by production brand settings.
        string settingsPath = (string)properties.GetField("_settingsPath", Static)!.GetValue(null)!;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        properties.GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(profile, null);
        var before = File.ReadAllBytes(settingsPath);
        var restore = settings.GetMethod("RestoreSessionWithPassword", Static)!;
        Check(!(bool)restore.Invoke(null, new object[] { "wrong password", true })!, "Wrong password accepted.");
        Check(before.SequenceEqual(File.ReadAllBytes(settingsPath)), "Wrong-password attempt modified the profile.");
        string malformed = (string)encryption.GetMethod("EncryptString", Static)!.Invoke(null, new[] { "invalid entry", hash })!;
        properties.GetProperty("ServerList")!.SetValue(profile, new[] { legacy, malformed });
        Check(!(bool)restore.Invoke(null, new object[] { password, true })!, "Invalid batch accepted.");
        Check(before.SequenceEqual(File.ReadAllBytes(settingsPath)), "Failed batch modified the profile.");
        properties.GetProperty("ServerList")!.SetValue(profile, new[] { legacy });
        using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Check((bool)restore.Invoke(null, new object[] { password, true })!, "Unlock should survive a migration write failure.");
        Check(before.SequenceEqual(File.ReadAllBytes(settingsPath)), "Failed migration modified the profile.");
        Check(((string[])properties.GetProperty("ServerList")!.GetValue(profile)!).Single() == legacy, "Failed migration replaced the in-memory legacy list.");
        Check((bool)restore.Invoke(null, new object[] { password, true })!, "Legacy unlock failed.");
        var migrated = (string[])properties.GetProperty("ServerList")!.GetValue(profile)!;
        Check(migrated.Single().StartsWith("wp2:600000:", StringComparison.Ordinal), "Legacy credentials were not migrated.");
        Check(File.ReadAllText(settingsPath).Contains("wp2:600000:", StringComparison.Ordinal), "Migration was not saved.");
        Check((bool)restore.Invoke(null, new object[] { password, true })!, "Versioned credentials could not be reopened.");
        before = File.ReadAllBytes(settingsPath);
        properties.GetProperty("ServerAddressList")!.SetValue(profile, Array.Empty<string>());
        Check((bool)restore.Invoke(null, new object?[] { null, false })!, "Cancelled restore should use addresses only.");
        Check(before.SequenceEqual(File.ReadAllBytes(settingsPath)), "Cancelled unlock modified credentials.");
        program.GetField("MainPassword", Static)!.SetValue(null, hash);
        var encrypt = settings.GetMethod("EncryptCredentials", Static)!;
        string saved = (string)encrypt.Invoke(null, new object?[] { "synthetic.invalid", 443, "user", "secret", true, "Friendly", null })!;
        Check(saved.StartsWith("wp2:600000:", StringComparison.Ordinal), "New credential used legacy protection.");
        before = File.ReadAllBytes(settingsPath);
        using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { properties.GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(profile, null); throw new Exception("Expected replacement failure."); }
            catch (TargetInvocationException error) when (error.InnerException is IOException) { }
        }
        Check(before.SequenceEqual(File.ReadAllBytes(settingsPath)), "Failed save damaged the profile.");
        Check(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "Settings temp files remain.");
    }

    /// <summary>Fails the isolated compatibility probe when an observed production behavior violates its expected invariant.</summary>
    /// <param name="condition">The invariant that must hold.</param>
    /// <param name="message">Failure detail without real credential values.</param>
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
