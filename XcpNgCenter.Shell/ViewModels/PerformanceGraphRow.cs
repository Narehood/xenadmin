using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using XcpNgCenter.Shell.Services.Performance;

namespace XcpNgCenter.Shell.ViewModels;

public sealed class PerformanceSeriesView
{
    public PerformanceSeriesView(
        string id,
        string name,
        string colorHex,
        IReadOnlyList<RrdPoint> points,
        string? units = null)
    {
        Id = id;
        Name = name;
        ColorHex = colorHex;
        Points = points;
        Units = units ?? "";
    }

    public string Id { get; }
    public string Name { get; }
    public string ColorHex { get; }
    public IReadOnlyList<RrdPoint> Points { get; }
    public string Units { get; }

    public bool IsPercentUnit =>
        Units is "percent" or "(fraction)"
        || Units.Contains("percent", StringComparison.OrdinalIgnoreCase);
}

public partial class PerformanceGraphRow : ObservableObject
{
    public PerformanceGraphRow(
        string title,
        IReadOnlyList<PerformanceSeriesView> series,
        RrdArchiveInterval interval,
        double? yAxisMax = null)
    {
        Title = title;
        Interval = interval;
        Series = new ObservableCollection<PerformanceSeriesView>(series);
        YAxisMax = yAxisMax;
        HasData = series.Any(s => s.Points.Count > 0);
        LatestSummary = BuildSummary(series);
    }

    public string Title { get; }

    public RrdArchiveInterval Interval { get; }

    /// <summary>
    /// When set (e.g. 100 for CPU %), the chart Y-axis uses this fixed maximum instead of the data peak.
    /// </summary>
    public double? YAxisMax { get; }

    public ObservableCollection<PerformanceSeriesView> Series { get; }

    public bool HasData { get; }

    public string LatestSummary { get; }

    private static string BuildSummary(IReadOnlyList<PerformanceSeriesView> series)
    {
        var parts = new List<string>();
        foreach (var s in series.Take(4))
        {
            var latest = s.Points.FirstOrDefault();
            if (latest.Ticks == 0 && latest.Value == 0 && s.Points.Count == 0)
                continue;
            if (s.Points.Count == 0)
                continue;
            parts.Add($"{s.Name}: {FormatValue(latest.Value)}");
        }

        return parts.Count == 0 ? "Waiting for samples…" : string.Join(" · ", parts);
    }

    private static string FormatValue(double value)
    {
        if (value < 0)
            return "—";
        if (value >= 1_000_000_000)
            return $"{value / 1_000_000_000:0.##}G";
        if (value >= 1_000_000)
            return $"{value / 1_000_000:0.##}M";
        if (value >= 1_000)
            return $"{value / 1_000:0.##}K";
        return $"{value:0.##}";
    }
}
