using System.Diagnostics;
using Avalonia;

namespace XcpNgCenter.Shell.Services;

public sealed partial class ShellUpdateInstaller
{
    private const string ApplyReadyFile = "apply-ready";
    private const string LegacyUpdateMessage = "This older build cannot install the new updater automatically. "
        + "Your installation has not been changed. Close XCP-ng Center, download the latest release from "
        + "https://github.com/Narehood/xenadmin/releases/latest and extract it into a new folder, then open XcpNgCenter.Shell. "
        + "Your saved settings will be retained.";

    private static string GetBrokerStartedPath(string cacheRoot, string launchRoot) =>
        GetBrokerAcknowledgementPath(cacheRoot, launchRoot) + ".started";

    // Only actual staged helper invocations open this UI. Invalid/headless probes
    // still follow the rejecting paths without initializing the desktop.
    public static bool TryRunUpdateProgressMode(string[] args, out int exitCode)
    {
        exitCode = 0;
        var broker = args.Contains(RestartBrokerArgument, StringComparer.Ordinal);
        var apply = args.Contains(ApplyArgument, StringComparer.Ordinal);
        if (!broker && !apply) return false;
        var root = GetArgumentValue(args, UpdateRootArgument);
        var install = GetArgumentValue(args, InstallDirectoryArgument);
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(install)) return false;
        try
        {
            if (!PathEquals(Environment.ProcessPath ?? string.Empty,
                    Path.Combine(root, PayloadDirectoryName, GetExecutableName(OperatingSystem.IsWindows())))) return false;
            var legacy = IsLegacyUpdateLocation(install, root);
            if (!legacy && !broker) return false;
            // The original user's broker handles recovery when the legacy installer
            // was elevated. Never reopen the full application with that admin token.
            if (legacy && apply && args.Contains(DeferRestartArgument, StringComparer.Ordinal)) return false;
            ShellUpdateProgressApplication.Operation = report =>
            {
                if (legacy)
                {
                    report(LegacyUpdateMessage);
                    // Authorize recovery using the live original process, not the
                    // untrusted legacy manifest or a supplied executable path.
                    if (!IsCurrentProcessElevated()
                        && int.TryParse(GetArgumentValue(args, WaitPidArgument), out var pid)
                        && pid > 0 && pid != Environment.ProcessId
                        && GetLiveProcessExecutable(pid) is { } parentExecutable
                        && PathEquals(parentExecutable, Path.Combine(install, GetExecutableName(OperatingSystem.IsWindows()))))
                    {
                        WaitForProcessExit(pid);
                        TryRestartApplication(parentExecutable, install, string.Empty, string.Empty, LegacyUpdateMessage);
                    }
                    return 1;
                }
                TryRunRestartBrokerMode(args, out var code, WaitForProcessExit, TryRestartApplication, report);
                return code;
            };
            exitCode = AppBuilder.Configure<ShellUpdateProgressApplication>().UsePlatformDetect()
                .StartWithClassicDesktopLifetime([]);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not display update progress: {ex}");
            exitCode = 1;
            return true;
        }
    }

    internal static bool IsLegacyUpdateLocation(string install, string root)
    {
        var normalized = NormalizeDirectory(root);
        var name = Path.GetFileName(normalized);
        return name.StartsWith('v') && Version.TryParse(name[1..], out var version) && version.Revision >= 0
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(normalized)),
                GetInstallDirectoryIdentity(install, OperatingSystem.IsWindows()), StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task WaitForHelperReadyAsync(string marker, Func<bool> hasExited, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (hasExited()) throw new InvalidOperationException("The update restart helper exited before it was ready. XCP-ng Center will stay open.");
            if (File.Exists(marker)) return;
            await Task.Delay(50).ConfigureAwait(false);
        }
        throw new TimeoutException("The update helper did not become ready. XCP-ng Center will stay open; retry the installation.");
    }
}
