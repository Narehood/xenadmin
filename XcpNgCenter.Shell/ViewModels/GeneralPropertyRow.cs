namespace XcpNgCenter.Shell.ViewModels;

public sealed class GeneralPropertyRow
{
    public GeneralPropertyRow(string label, string value)
    {
        Label = label;
        Value = value;
        CanCopy = !string.IsNullOrWhiteSpace(value)
                  && value != "—"
                  && !string.Equals(value, "n/a", StringComparison.OrdinalIgnoreCase);
    }

    public string Label { get; }
    public string Value { get; }
    public bool CanCopy { get; }
}
