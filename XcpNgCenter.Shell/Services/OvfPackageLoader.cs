using XenOvf;

namespace XcpNgCenter.Shell.Services;

internal sealed record OvfPackageLoadResult(Package? Package, IReadOnlyList<string> Systems,
    IReadOnlyList<string> Warnings, string? Error = null);

internal static class OvfPackageLoader
{
    public static Task<OvfPackageLoadResult> LoadAsync(string path, CancellationToken token)
        => LoadAsync(path, token, Package.Create);

    internal static Task<OvfPackageLoadResult> LoadAsync(string path, CancellationToken token,
        Func<string, CancellationToken, Package> createPackage)
        => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var package = createPackage(path, token);
            var valid = OVF.Validate(package, out var warnings);
            // The legacy validator converts some I/O exceptions into validation errors.
            token.ThrowIfCancellationRequested();
            if (!valid)
                return new OvfPackageLoadResult(null, [], [], warnings?.LastOrDefault()
                    ?? "The appliance did not pass OVF validation.");

            var envelope = package.OvfEnvelope
                           ?? throw new InvalidOperationException("Appliance has no OVF envelope.");
            var systems = new List<string>();
            foreach (var id in OVF.FindSystemIds(envelope))
            {
                token.ThrowIfCancellationRequested();
                var name = OVF.FindSystemName(envelope, id);
                systems.Add($"{(string.IsNullOrWhiteSpace(name) ? id : name)} ({id})");
            }
            return new OvfPackageLoadResult(package, systems,
                warnings?.Where(w => !string.IsNullOrWhiteSpace(w)).ToArray() ?? []);
        }, token);
}
