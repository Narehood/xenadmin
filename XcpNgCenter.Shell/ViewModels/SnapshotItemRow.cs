using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace XcpNgCenter.Shell.ViewModels;

public partial class SnapshotItemRow : ObservableObject
{
    public SnapshotItemRow(
        string name,
        string description,
        string created,
        string opaqueRef,
        bool isNow = false,
        bool isBase = false,
        Bitmap? icon = null)
    {
        Name = name;
        Description = description;
        Created = created;
        OpaqueRef = opaqueRef;
        IsNow = isNow;
        IsBase = isBase;
        Icon = icon;
    }

    public string Name { get; }
    public string Description { get; }
    public string Created { get; }
    public string OpaqueRef { get; }
    public bool IsNow { get; }
    public bool IsBase { get; }
    public Bitmap? Icon { get; }

    public bool CanRevert => !IsNow && !IsBase && !string.IsNullOrEmpty(OpaqueRef);
    public bool CanDelete => !IsNow && !IsBase && !string.IsNullOrEmpty(OpaqueRef);

    public ObservableCollection<SnapshotItemRow> Children { get; } = new();
}
