using XenAdmin.Actions;

namespace XcpNgCenter.Shell.ViewModels;

public sealed record SnapshotTypeOption(SnapshotType Type, string Label, string Detail)
{
    public override string ToString() => Label;
}
