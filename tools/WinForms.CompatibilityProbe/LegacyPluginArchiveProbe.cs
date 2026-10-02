using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml;

internal static class LegacyPluginArchiveProbe
{
    // Exercise the actual manifest loader without starting the app, reading profiles,
    // loading plugin resource DLLs, launching commands or contacting a hypervisor.
    public static int Run(Assembly assembly)
    {
        var failures = new List<string>();
        var checks = 0;
        void Check(string label, Action action)
        {
            try { action(); checks++; }
            catch (Exception error) { failures.Add($"{label}: {error}"); }
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        foreach (var name in new[]
        {
            "XenAdmin.Plugins.TabPageFeature", "XenAdmin.Core.WebBrowser2",
            "XenAdmin.Plugins.TabPageCredentialsDialog", "XenAdmin.Plugins.ScriptingObject"
        })
            Check($"Excluded type {name}", () => Require(assembly.GetType(name) == null, "Archived type is still compiled."));
        Check("Excluded credentials resources", () => Require(!assembly.GetManifestResourceNames()
            .Any(n => n.Contains("TabPageCredentialsDialog", StringComparison.Ordinal)), "Archived dialog resources are embedded."));

        var poolType = Assembly.Load(assembly.GetReferencedAssemblies().Single(n => n.Name == "XenModel"))
            .GetType("XenAPI.Pool", throwOnError: true)!;
        foreach (var name in new[] { "GetXCPluginSecret", "SetXCPluginSecret", "RemoveXCPluginSecret", "XCPluginSecretName" })
            Check($"Excluded secret helper {name}", () => Require(poolType.GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) == null, "Archived secret helper is still compiled."));

        var descriptorType = assembly.GetType("XenAdmin.Plugins.PluginDescriptor", throwOnError: true)!;
        var managerType = assembly.GetType("XenAdmin.Plugins.PluginManager", throwOnError: true)!;
        var mainType = assembly.GetType("XenAdmin.MainWindow", throwOnError: true)!;
        foreach (var (type, name) in new[]
        {
            (descriptorType, "DisposeURLs"), (managerType, "DisposeURLs"),
            (mainType, "GetFeatureTabPages"), (mainType, "UpdateTabePageFeatures")
        })
            Check($"Excluded integration {name}", () => Require(type.GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) == null, "Archived UI integration is still compiled."));

        object Load(string elements)
        {
            var descriptor = RuntimeHelpers.GetUninitializedObject(descriptorType);
            foreach (var name in new[] { "_features", "_searches", "_methodLists" })
            {
                var field = descriptorType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
                field.SetValue(descriptor, Activator.CreateInstance(field.FieldType));
            }
            descriptorType.GetField("_name", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(descriptor, "archive-probe");
            descriptorType.GetField("_organization", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(descriptor, "synthetic");
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml($"<Plugin>{elements}</Plugin>");
            descriptorType.GetMethod("LoadFeatures", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(descriptor, [document.DocumentElement]);
            // Reproduce opt-in and validation without accessing registry or app settings.
            descriptorType.GetProperty("Enabled")!.SetValue(descriptor, true);
            descriptorType.GetMethod("ValidateAll", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(descriptor, null);
            return descriptor;
        }
        object[] Features(object descriptor) => ((IEnumerable)descriptorType.GetProperty("Features")!
            .GetValue(descriptor)!).Cast<object>().ToArray();
        string? Error(object descriptor) => (string?)descriptorType.GetProperty("Error")!.GetValue(descriptor);
        bool Enabled(object descriptor) => (bool)descriptorType.GetProperty("Enabled")!.GetValue(descriptor)!;

        const string menu = "<MenuItem name='synthetic-action' menu='tools'><Shell filename='synthetic-never-run.exe' /></MenuItem>";
        const string tab = "<TabPage name='old-tab' url='https://example.invalid/' />";
        foreach (var elements in new[] { tab, "<TabPage />", tab + tab })
            Check("Tab-only manifest disabled", () =>
            {
                var descriptor = Load(elements);
                Require(Features(descriptor).Length == 0, "Tab-only manifest created a feature.");
                Require(!Enabled(descriptor), "Tab-only plugin was enabled.");
                Require(Error(descriptor)?.Contains("archived", StringComparison.Ordinal) == true, "Archive reason is missing.");
            });

        foreach (var elements in new[] { menu, tab + menu, menu + tab, "<TabPage />" + menu + tab })
            Check("Menu commands preserved", () =>
            {
                var descriptor = Load(elements);
                Require(Error(descriptor) == null && Enabled(descriptor), "Valid menu plugin was disabled.");
                var features = Features(descriptor);
                Require(features.Length == 1 && features[0].GetType().FullName == "XenAdmin.Plugins.MenuItemFeature", "Menu feature was lost or a tab was instantiated.");
                Require((string?)features[0].GetType().GetProperty("Name")!.GetValue(features[0]) == "synthetic-action", "Menu identity changed.");
                var command = features[0].GetType().GetField("ShellCmd")!.GetValue(features[0])!;
                Require((string?)command.GetType().GetField("Filename")!.GetValue(command) == "synthetic-never-run.exe", "Command target changed.");
                descriptorType.GetProperty("Enabled")!.SetValue(descriptor, false);
            });

        Check("Grouped menu preserved beside archived tab", () =>
        {
            var descriptor = Load(tab + $"<GroupMenuItem name='synthetic-group' menu='tools'>{menu}</GroupMenuItem>");
            var features = Features(descriptor);
            Require(Error(descriptor) == null && Enabled(descriptor) && features.Length == 1, "Grouped menu was disabled or lost.");
            Require(features[0].GetType().FullName == "XenAdmin.Plugins.ParentMenuItemFeature", "Wrong grouped feature type.");
            Require(((IEnumerable)features[0].GetType().GetProperty("Features")!.GetValue(features[0])!).Cast<object>().Count() == 1, "Nested command was lost.");
            descriptorType.GetProperty("Enabled")!.SetValue(descriptor, false);
        });
        Check("Unknown features still rejected", () =>
        {
            var descriptor = Load(menu + "<UnexpectedFeature />" + tab);
            Require(!Enabled(descriptor) && Error(descriptor)?.Contains("UnexpectedFeature", StringComparison.Ordinal) == true, "Unknown feature was silently accepted.");
        });
        Check("Invalid menu still rejected beside archived tab", () =>
        {
            var descriptor = Load(tab + "<MenuItem name='invalid' menu='tools'><Shell /></MenuItem>");
            Require(!Enabled(descriptor) && !string.IsNullOrEmpty(Error(descriptor)), "Invalid retained command was accepted.");
        });

        Console.WriteLine($"Plugin archive: {checks} passing checks, {failures.Count} failures.");
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        return failures.Count == 0 ? 0 : 1;
    }
}
