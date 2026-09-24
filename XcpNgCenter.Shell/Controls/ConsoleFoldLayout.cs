namespace XcpNgCenter.Shell.Controls;

/// <summary>
/// Chooses the embedded console host height.
/// Scale-to-fit keeps the framebuffer inside the visible fold. Native 1:1 mode
/// grows the host to the desktop so the same scroll viewer can reach the rest.
/// </summary>
internal static class ConsoleFoldLayout
{
    /// <summary>Embedded console border: thickness 1 plus padding 6, top and bottom.</summary>
    public const double EmbeddedChrome = 14;

    public static double? SelectFoldHeight(double viewportHeight, double arrangedHeight)
    {
        var viewport = Usable(viewportHeight);
        var arranged = Usable(arrangedHeight);
        if (viewport is double visible && arranged is double slot)
            return Math.Min(visible, slot);
        return viewport ?? arranged;
    }

    /// <summary>
    /// Host height for the current console mode. Scale-to-fit stays inside the fold.
    /// Native mode is at least the fold and tall enough for the unscaled desktop plus chrome.
    /// </summary>
    public static double? SelectHostHeight(
        double viewportHeight,
        double arrangedHeight,
        bool scaleToFit,
        double nativeDesktopDipHeight)
    {
        var fold = SelectFoldHeight(viewportHeight, arrangedHeight);
        if (fold is not double visible)
            return null;
        if (scaleToFit || Usable(nativeDesktopDipHeight) is not double native)
            return visible;
        return Math.Max(visible, native);
    }

    /// <summary>DIP height of a 1:1 framebuffer, including the embedded console chrome.</summary>
    public static double NativeHostDipHeight(int desktopPixels, double renderScaling)
    {
        if (desktopPixels <= 0)
            return 0;
        var dpi = renderScaling > 0 && !double.IsNaN(renderScaling) && !double.IsInfinity(renderScaling)
            ? renderScaling
            : 1;
        return desktopPixels / dpi + EmbeddedChrome;
    }

    private static double? Usable(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 1)
            return null;
        return value;
    }
}
