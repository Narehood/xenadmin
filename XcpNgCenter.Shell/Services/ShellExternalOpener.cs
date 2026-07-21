using System.Diagnostics;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Opens URLs / files with the OS default handler (xdg-open on Linux).
/// </summary>
public static class ShellExternalOpener
{
    public static bool TryOpenUrl(string url, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            error = "URL is empty.";
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
                return true;
            }

            // Linux / BSD: prefer xdg-open; fall back to UseShellExecute.
            try
            {
                Process.Start(new ProcessStartInfo("xdg-open", url)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                });
                return true;
            }
            catch
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
