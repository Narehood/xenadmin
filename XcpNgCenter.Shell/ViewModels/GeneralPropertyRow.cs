namespace XcpNgCenter.Shell.ViewModels;

public sealed class GeneralPropertyRow
{
    public GeneralPropertyRow(string label, string value, bool? canCopy = null)
    {
        Label = label;
        Value = value;
        CanCopy = canCopy ?? ComputeDefaultCanCopy(value);
    }

    public string Label { get; }
    public string Value { get; }
    public bool CanCopy { get; }

    private static bool ComputeDefaultCanCopy(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "—")
            return false;
        if (string.Equals(value, "n/a", StringComparison.OrdinalIgnoreCase))
            return false;
        if (Services.IdentifierPrivacy.IsRedacted(value))
            return false;
        return true;
    }
}
