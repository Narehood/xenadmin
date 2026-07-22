namespace XcpNgCenter.Shell.ViewModels;

public sealed class NetworkItemRow
{
    public NetworkItemRow(
        string primary,
        string secondary,
        string detail,
        string size,
        string meta)
    {
        Primary = primary;
        Secondary = secondary;
        Detail = detail;
        Size = size;
        Meta = meta;
    }

    public string Primary { get; }
    public string Secondary { get; }
    public string Detail { get; }
    public string Size { get; }
    public string Meta { get; }
}
