namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Decides when the shell should replace the live RFB session.
/// Guest VM reboots must reconnect (new console, new domid, or dropped RFB).
/// Host control-domain sessions must not retry after drop — holding HTTP CONNECT
/// stalls host reboot/shutdown (the previous Center-only hang).
/// </summary>
public static class ConsoleSessionSyncPolicy
{
    public static string SessionKey(string hostname, string opaqueRef, string location, long domainId)
        => $"{hostname}|{opaqueRef}|{location}|{domainId}";

    /// <summary>
    /// Prefer the last advertised RFB console. After a guest reboot xapi keeps the
    /// old console open (crash/BIOS text) and appends a replacement; staying on the
    /// first entry leaves the last pre-reboot frame frozen.
    /// </summary>
    public static TConsole? SelectPreferredRfbConsole<TConsole>(
        IEnumerable<TConsole> consoles,
        Func<TConsole, bool> isRfb,
        Func<TConsole, string?> location)
    {
        TConsole? preferred = default;
        foreach (var console in consoles)
        {
            if (isRfb(console) && !string.IsNullOrWhiteSpace(location(console)))
                preferred = console;
        }

        return preferred;
    }

    public static bool ShouldReplaceSession(
        string? activeKey,
        string candidateKey,
        bool sessionConnected,
        bool sessionConnecting,
        bool isControlDomain)
    {
        if (string.IsNullOrEmpty(candidateKey))
            return false;

        if (!string.Equals(activeKey, candidateKey, StringComparison.Ordinal))
            return true;

        if (sessionConnected || sessionConnecting)
            return false;

        // Same console target, transport is dead. Retry guests so a reboot does not
        // leave the last frame stuck; do not retry host consoles.
        return !isControlDomain;
    }
}
