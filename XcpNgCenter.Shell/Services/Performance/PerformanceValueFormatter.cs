namespace XcpNgCenter.Shell.Services.Performance;

public static class PerformanceValueFormatter
{
    public static string Format(double value, string? units = null)
    {
        if (!double.IsFinite(value) || value < 0)
            return "—";
        if (units == "bytes")
        {
            if (value >= 1L << 40) return $"{value / (1L << 40):0.##} TiB";
            if (value >= 1L << 30) return $"{value / (1L << 30):0.##} GiB";
            if (value >= 1L << 20) return $"{value / (1L << 20):0.##} MiB";
            if (value >= 1L << 10) return $"{value / (1L << 10):0.##} KiB";
            return $"{value:0.##} B";
        }
        if (value >= 1_000_000_000) return $"{value / 1_000_000_000:0.##}G";
        if (value >= 1_000_000) return $"{value / 1_000_000:0.##}M";
        if (value >= 1_000) return $"{value / 1_000:0.##}K";
        return $"{value:0.##}";
    }
}
