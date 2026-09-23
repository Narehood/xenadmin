namespace XcpNgCenter.Shell.Controls;

/// <summary>
/// Chooses the embedded console host height so the framebuffer fits in the visible fold.
/// </summary>
internal static class ConsoleFoldLayout
{
    /// <summary>
    /// Returns a host height that stays inside the scroll viewport. A fixed host taller than
    /// the fold hides the bottom of the guest screen until the page is scrolled or the window grows.
    /// </summary>
    public static double? SelectFoldHeight(double viewportHeight, double arrangedHeight)
    {
        var viewport = Usable(viewportHeight);
        var arranged = Usable(arrangedHeight);
        if (viewport is double visible && arranged is double slot)
            return Math.Min(visible, slot);
        return viewport ?? arranged;
    }

    private static double? Usable(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 1)
            return null;
        return value;
    }
}
