namespace XcpNgCenter.Shell.ViewModels;

public sealed class GeneralPropertyRow
{
    public GeneralPropertyRow(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public string Value { get; }
}
