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
                using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                using var _ = Process.Start(new ProcessStartInfo("open", QuoteArg(url))
                {
                    UseShellExecute = false
                });
                return true;
            }

            // Linux / BSD: prefer xdg-open; fall back to UseShellExecute.
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { url },
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                });

                if (process == null)
                {
                    error = "xdg-open failed to start.";
                    return false;
                }

                // xdg-open usually exits quickly after handing off; wait briefly for early failures.
                if (process.WaitForExit(1500))
                {
                    if (process.ExitCode != 0)
                    {
                        var stderr = process.StandardError.ReadToEnd().Trim();
                        error = string.IsNullOrWhiteSpace(stderr)
                            ? $"xdg-open exited with code {process.ExitCode}."
                            : stderr;
                        return false;
                    }
                }

                return true;
            }
            catch (Exception)
            {
                using var fallback = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string QuoteArg(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
