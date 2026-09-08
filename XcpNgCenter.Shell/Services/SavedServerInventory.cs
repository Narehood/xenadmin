using XenAdmin;
using XenCenterLib;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

internal static class SavedServerInventory
{
    // Metadata only: this must also work with locked or missing saved passwords.
    internal static IEnumerable<ServerNode> Restore(IEnumerable<SavedServerEntry> entries)
    {
        var endpoints = new HashSet<(string Host, int Port)>();
        foreach (var entry in entries)
        {
            if (!HostnameAddressClassifier.TryParseHostPort(entry.Address, out var host, out var port))
                continue;
            var effectivePort = port > 0 ? port : ConnectionsManager.DEFAULT_XEN_PORT;
            if (!endpoints.Add((host.ToUpperInvariant(), effectivePort)))
                continue;

            yield return new ServerNode
            {
                Name = host,
                Address = entry.Address,
                Hostname = host,
                Port = effectivePort,
                Username = entry.Username,
                IsPublicIp = HostnameAddressClassifier.IsPublicIp(host),
                Status = "Saved",
                Summary = "Disconnected — right-click to reconnect"
            };
        }
    }
}
