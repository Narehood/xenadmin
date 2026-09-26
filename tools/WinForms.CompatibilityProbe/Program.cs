using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

// Run against a built application without opening it or reading a user's profile.
if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Pass the full path to the built XCP-ng Center.dll.");

var assemblyPath = Path.GetFullPath(args[0]);
var resolver = new AssemblyDependencyResolver(assemblyPath);
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = resolver.ResolveAssemblyToPath(name);
    return path == null ? null : context.LoadFromAssemblyPath(path);
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
var failures = new List<string>();
var resourceCount = 0;
var setCount = 0;
foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".resources", StringComparison.Ordinal)))
{
    var manager = new ResourceManager(name[..^".resources".Length], assembly);
    try
    {
        var resources = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)
            ?? throw new InvalidOperationException("Resource set was not found.");
        foreach (DictionaryEntry entry in resources)
        {
            _ = entry.Value; // Force deserialization, including legacy ImageList/ListView/ActiveX resources.
            resourceCount++;
        }
        setCount++;
    }
    catch (Exception error)
    {
        failures.Add($"{name}: {error}");
    }
    finally
    {
        manager.ReleaseAllResources();
    }
}

try
{
    RuntimeHelpers.RunClassConstructor(assembly.GetType("XenAdmin.Settings", throwOnError: true)!.TypeHandle);
}
catch (Exception error)
{
    failures.Add($"Settings initialization: {error}");
}

try
{
    var streamType = assembly.GetType("DotNetVnc.MyStream", throwOnError: true)!;
    using var input = new FragmentedReadStream(new byte[] { 0, 0, 0, 0x7a });
    var stream = Activator.CreateInstance(streamType, input)!;
    streamType.GetMethod("readPadding")!.Invoke(stream, new object[] { 3 });
    var next = (int)streamType.GetMethod("readCard8")!.Invoke(stream, null)!;
    if (next != 0x7a)
        failures.Add("RFB padding did not consume all bytes from a fragmented stream.");
}
catch (Exception error)
{
    failures.Add($"RFB fragmented reads: {error}");
}

if (AppContext.TryGetSwitch("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", out var unsafeSerialization) && unsafeSerialization)
    failures.Add("Unsafe BinaryFormatter serialization must remain disabled.");
if (AppContext.TryGetSwitch("System.Resources.Extensions.UseBinaryFormatter", out var unsafeResources) && unsafeResources)
    failures.Add("Resource loading must not use the BinaryFormatter compatibility path.");
if (setCount == 0 || resourceCount == 0)
    failures.Add("No application resources were exercised.");

Console.WriteLine($"Loaded {resourceCount} resources from {setCount} sets on {Environment.Version}.");
foreach (var failure in failures)
    Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

sealed class FragmentedReadStream(byte[] bytes) : MemoryStream(bytes)
{
    public override int Read(byte[] buffer, int offset, int count) =>
        base.Read(buffer, offset, Math.Min(count, 1));
}
