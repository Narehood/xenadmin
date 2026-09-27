namespace XcpNgCenter.Shell.ViewModels;

public sealed class NetworkItemRow
{
    public NetworkItemRow(
        string primary,
        string secondary,
        string detail,
        string size,
        string meta,
        XenAPI.IXenObject? target = null,
        string tags = "")
    {
        Primary = primary;
        Secondary = secondary;
        Detail = detail;
        Size = size;
        Meta = meta;
        Target = target;
        Tags = tags;
    }

    public string Primary { get; }
    public string Secondary { get; }
    public string Detail { get; }
    public string Size { get; }
    public string Meta { get; }
    public XenAPI.IXenObject? Target { get; }
    public string Tags { get; }
    public bool HasTags => Tags.Length > 0;
    public bool HasActions => Target != null;
    public bool IsVmInterface => Target is XenAPI.VIF;
    public bool IsBond => Target is XenAPI.Network network && Services.BondManagement.IsBondNetwork(network);
    public string? BondError => Target is XenAPI.Network network ? Services.BondManagement.ExistingError(network) : "Select a bond network.";
    public bool CanManageBond => IsBond && BondError == null;
    public string ToggleLabel => Target is XenAPI.VIF { currently_attached: true } ? "Disconnect" : "Connect";
    public string? EditError => Target switch
    {
        XenAPI.Network network => Services.NetworkManagement.EditNetworkError(network),
        XenAPI.VIF vif => Services.NetworkManagement.VifError(vif),
        _ => "Select a network or interface."
    };
    public string? RemoveError => Target is XenAPI.Network network
        ? network.IsSriov() ? Services.SriovNetworkManagement.RemovalError(network) : Services.NetworkManagement.RemoveNetworkError(network)
        : EditError;
    public bool CanEdit => EditError == null;
    public bool CanRemove => RemoveError == null;
    public bool CanToggle => Target is XenAPI.VIF vif && Services.NetworkManagement.VifError(vif, true) == null;
}
