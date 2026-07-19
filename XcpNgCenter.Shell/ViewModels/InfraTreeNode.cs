using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace XcpNgCenter.Shell.ViewModels;

public enum InfraNodeKind
{
    Pool,
    Host,
    Vm,
    Group
}

public partial class InfraTreeNode : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private bool _isExpanded = true;

    public InfraNodeKind Kind { get; init; }

    public string KindLabel => Kind switch
    {
        InfraNodeKind.Pool => "Pool",
        InfraNodeKind.Host => "Host",
        InfraNodeKind.Vm => "VM",
        InfraNodeKind.Group => "Group",
        _ => string.Empty
    };

    public ServerNode? Server { get; init; }

    public string? OpaqueRef { get; init; }

    public ObservableCollection<InfraTreeNode> Children { get; } = new();
}
