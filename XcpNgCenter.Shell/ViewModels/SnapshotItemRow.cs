namespace XcpNgCenter.Shell.ViewModels;

public sealed class SnapshotItemRow
{
    public SnapshotItemRow(string name, string description, string created, string opaqueRef)
    {
        Name = name;
        Description = description;
        Created = created;
        OpaqueRef = opaqueRef;
    }

    public string Name { get; }
    public string Description { get; }
    public string Created { get; }
    public string OpaqueRef { get; }
}
