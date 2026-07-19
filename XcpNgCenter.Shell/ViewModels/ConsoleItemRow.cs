namespace XcpNgCenter.Shell.ViewModels;

public sealed class ConsoleItemRow
{
    public ConsoleItemRow(
        string primary,
        string secondary,
        string detail,
        string location,
        bool canCopyLocation)
    {
        Primary = primary;
        Secondary = secondary;
        Detail = detail;
        Location = location;
        CanCopyLocation = canCopyLocation;
    }

    public string Primary { get; }
    public string Secondary { get; }
    public string Detail { get; }
    public string Location { get; }
    public bool CanCopyLocation { get; }
}
