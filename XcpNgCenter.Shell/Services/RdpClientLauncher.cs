using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using XenAdmin.Network;
using XenAPI;

namespace XcpNgCenter.Shell.Services;

public sealed record RdpEndpoint
{
    private RdpEndpoint(IPAddress address, int port) { Address = address; Port = port; }
    public IPAddress Address { get; }
    public int Port { get; }
    public string Authority => Address.AddressFamily == AddressFamily.InterNetworkV6
        ? $"[{Address}]:{Port}" : $"{Address}:{Port}";

    public static bool TryCreate(string? addressText, string? portText, out RdpEndpoint? endpoint)
    {
        endpoint = null;
        var text = addressText?.Trim();
        if (string.IsNullOrEmpty(text) || text.Any(c => !char.IsAsciiHexDigit(c) && c is not ('.' or ':'))
            || !IPAddress.TryParse(text, out var address)
            || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
            return false;
        // Avoid the legacy shorthand, octal and integer forms accepted by IPAddress.
        if (address.AddressFamily == AddressFamily.InterNetwork && text != address.ToString()) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6Multicast || address.IsIPv6LinkLocal
            || (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] >= 224))
            return false;
        endpoint = new RdpEndpoint(address, port);
        return true;
    }
}

/// <summary>Identity reviewed before the dialog; cache updates may replace the VM instance.</summary>
public sealed record RdpTargetReview(IXenConnection Connection, string OpaqueRef, string Uuid)
{
    public static bool IsRunningGuest(VM? vm) => vm is
        { power_state: vm_power_state.Running, is_control_domain: false, is_a_template: false, is_a_snapshot: false }
        && !string.IsNullOrWhiteSpace(vm.opaque_ref) && !string.IsNullOrWhiteSpace(vm.uuid);
    public bool Matches(VM? vm) => IsRunningGuest(vm) && ReferenceEquals(vm!.Connection, Connection)
        && vm.opaque_ref == OpaqueRef && vm.uuid == Uuid;
}

public enum RdpClientKind { WindowsRemoteDesktop, Remmina }

/// <summary>Launches an interactive client with only a reviewed guest endpoint.</summary>
public static class RdpClientLauncher
{
    public static IReadOnlyList<string> GuestAddresses(VM vm)
        => (vm.Connection.Resolve(vm.guest_metrics)?.networks ?? new Dictionary<string, string>())
            .Where(pair => pair.Key.Contains("/ip", StringComparison.Ordinal))
            .Select(pair => RdpEndpoint.TryCreate(pair.Value, "3389", out var endpoint) ? endpoint!.Address.ToString() : null)
            .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static ProcessStartInfo CreateStartInfo(RdpEndpoint endpoint, RdpClientKind kind)
    {
        var info = new ProcessStartInfo { UseShellExecute = false };
        if (kind == RdpClientKind.WindowsRemoteDesktop)
        {
            info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mstsc.exe");
            info.ArgumentList.Add($"/v:{endpoint.Authority}");
            info.ArgumentList.Add("/prompt");
        }
        else
        {
            info.FileName = "/usr/bin/remmina";
            info.ArgumentList.Add("--connect");
            info.ArgumentList.Add($"rdp://{endpoint.Authority}");
        }
        return info;
    }

    public static bool TryLaunch(RdpEndpoint endpoint, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            error = "Remote Desktop launch is available on Windows and Linux.";
            return false;
        }
        var kind = OperatingSystem.IsWindows() ? RdpClientKind.WindowsRemoteDesktop : RdpClientKind.Remmina;
        var info = CreateStartInfo(endpoint, kind);
        if (!File.Exists(info.FileName))
        {
            error = kind == RdpClientKind.Remmina
                ? "Install Remmina with its RDP plugin (system package at /usr/bin/remmina), then try again."
                : "Windows Remote Desktop Connection (mstsc.exe) is not installed.";
            return false;
        }
        try
        {
            using var process = Process.Start(info);
            if (process != null) return true;
            error = "The Remote Desktop client could not be started.";
        }
        catch (Exception ex) { error = ex.Message; }
        return false;
    }
}
