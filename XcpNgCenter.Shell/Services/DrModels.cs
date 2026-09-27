namespace XcpNgCenter.Shell.Services;

public enum DrMode { Recovery, MetadataRehearsal }
public sealed record DrMetadataOption(string Reference, string Uuid, string Name, string Storage);
public sealed record DrStorageOption(string Reference, string Uuid, string Name);
public sealed record DrNetworkOption(string Reference, string Uuid, string Name, bool IsIsolated);
public sealed record DrSourceStorage(string Reference, string Uuid, string Name);
public sealed record DrSourceNetwork(string Reference, string Uuid, string Name);
public sealed record DrVmOption(string Reference, string Uuid, string Name, string? UnavailableReason,
    IReadOnlyList<string> StorageReferences, IReadOnlyList<string> NetworkReferences);
public sealed record DrStorageMapping(string SourceReference, string TargetReference);
public sealed record DrNetworkMapping(string SourceReference, string TargetReference);

public sealed class DrInspection
{
    public DrInspection(DrMetadataOption metadata, string sourcePoolName, string sourcePoolUuid,
        IReadOnlyList<DrVmOption> vms, IReadOnlyList<DrSourceStorage> sourceStorage,
        IReadOnlyList<DrSourceNetwork> sourceNetworks, IReadOnlyList<DrStorageOption> targetStorage,
        IReadOnlyList<DrNetworkOption> targetNetworks)
    {
        Metadata = metadata; SourcePoolName = sourcePoolName; SourcePoolUuid = sourcePoolUuid;
        Vms = Array.AsReadOnly(vms.ToArray()); SourceStorage = Array.AsReadOnly(sourceStorage.ToArray());
        SourceNetworks = Array.AsReadOnly(sourceNetworks.ToArray()); TargetStorage = Array.AsReadOnly(targetStorage.ToArray());
        TargetNetworks = Array.AsReadOnly(targetNetworks.ToArray());
    }
    public DrMetadataOption Metadata { get; }
    public string SourcePoolName { get; }
    public string SourcePoolUuid { get; }
    public IReadOnlyList<DrVmOption> Vms { get; }
    public IReadOnlyList<DrSourceStorage> SourceStorage { get; }
    public IReadOnlyList<DrSourceNetwork> SourceNetworks { get; }
    public IReadOnlyList<DrStorageOption> TargetStorage { get; }
    public IReadOnlyList<DrNetworkOption> TargetNetworks { get; }
    internal string Fingerprint { get; init; } = "";
}

public sealed class DrRequest
{
    public DrRequest(DrMode mode, IEnumerable<string> vmReferences, IEnumerable<DrStorageMapping> storage,
        IEnumerable<DrNetworkMapping> networks)
    {
        Mode = mode; VmReferences = Array.AsReadOnly(vmReferences.ToArray());
        Storage = Array.AsReadOnly(storage.ToArray()); Networks = Array.AsReadOnly(networks.ToArray());
    }
    public DrMode Mode { get; }
    public IReadOnlyList<string> VmReferences { get; }
    public IReadOnlyList<DrStorageMapping> Storage { get; }
    public IReadOnlyList<DrNetworkMapping> Networks { get; }
    internal string Fingerprint => DrManagement.Hash(new { Mode, VmReferences, Storage, Networks });
}

public sealed record DrReview(string RequestFingerprint, string SourceFingerprint, string TargetFingerprint, string Summary);
public sealed record DrVmResult(string Name, string Uuid, string Status, bool Recovered);
public sealed record DrCleanupItem(string Reference, string Uuid, string Name, string Fingerprint);

public sealed class DrOutcome
{
    public DrOutcome(DrMode mode, IEnumerable<DrVmResult> results, IEnumerable<DrCleanupItem> cleanup, string? error = null)
    { Mode = mode; Results = Array.AsReadOnly(results.ToArray()); Cleanup = Array.AsReadOnly(cleanup.ToArray()); Error = error; }
    public DrMode Mode { get; }
    public IReadOnlyList<DrVmResult> Results { get; }
    public IReadOnlyList<DrCleanupItem> Cleanup { get; }
    public string? Error { get; }
    public bool Succeeded => Error == null;
    public string Report => string.Join(Environment.NewLine, Results.Select(vm => $"{vm.Name} [{vm.Uuid}]: {vm.Status}"))
        + (Error == null ? "" : $"{Environment.NewLine}{Error}");
}

public interface IDrWorkflow
{
    string PoolName { get; }
    Task<IReadOnlyList<DrMetadataOption>> DiscoverAsync();
    Task<DrInspection> InspectAsync(DrMetadataOption metadata);
    Task<DrReview> ReviewAsync(DrInspection inspection, DrRequest request);
    Task<DrOutcome> RecoverAsync(DrInspection inspection, DrRequest request, DrReview review);
    Task<DrOutcome> CleanupAsync(DrOutcome outcome);
}
