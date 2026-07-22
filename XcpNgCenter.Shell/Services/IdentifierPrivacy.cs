namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Display-time redaction for in-app identifiers (screenshots / demos).
/// Raw values stay in memory for connect/API; only UI strings are masked.
/// </summary>
public static class IdentifierPrivacy
{
    public const string Placeholder = "••••••••";

    private static ShellAppSettings? _settings;

    public static void Bind(ShellAppSettings settings) => _settings = settings;

    public static bool HideIpAddresses => _settings?.HideIpAddresses == true;
    public static bool HideUuids => _settings?.HideUuids == true;
    public static bool HideVmNames => _settings?.HideVmNames == true;
    public static bool HideServerNames => _settings?.HideServerNames == true;
    public static bool HideClusterNames => _settings?.HideClusterNames == true;

    public static string ClusterName(string? value) => Mask(value, HideClusterNames);
    public static string ServerName(string? value) => Mask(value, HideServerNames);
    public static string VmName(string? value) => Mask(value, HideVmNames);
    public static string Address(string? value) => Mask(value, HideIpAddresses);
    public static string Uuid(string? value) => Mask(value, HideUuids);

    public static string Mask(string? value, bool hide)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "—")
            return value ?? string.Empty;
        return hide ? Placeholder : value;
    }

    public static bool IsRedacted(string? value)
        => !string.IsNullOrEmpty(value) && value == Placeholder;
}
