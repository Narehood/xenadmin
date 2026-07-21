using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Controls;

/// <summary>
/// Lightweight multi-series sparkline for Host/VM RRD data (no WinForms GDI).
/// </summary>
public sealed class PerformanceChart : Control
{
    public static readonly StyledProperty<IEnumerable<PerformanceSeriesView>?> SeriesProperty =
        AvaloniaProperty.Register<PerformanceChart, IEnumerable<PerformanceSeriesView>?>(nameof(Series));

    static PerformanceChart()
    {
        AffectsRender<PerformanceChart>(SeriesProperty, BoundsProperty);
    }

    public IEnumerable<PerformanceSeriesView>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width < 8 || bounds.Height < 8)
            return;

        var pad = 8.0;
        var plot = new Rect(pad, pad, Math.Max(1, bounds.Width - pad * 2), Math.Max(1, bounds.Height - pad * 2));

        context.FillRectangle(new SolidColorBrush(Color.Parse("#1A222A")), plot, 4);

        var seriesList = Series?.Where(s => s.Points.Count > 1).ToList() ?? [];
        if (seriesList.Count == 0)
        {
            var msg = new FormattedText(
                "No samples yet",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Outfit"),
                12,
                new SolidColorBrush(Color.Parse("#9AA6B2")));
            context.DrawText(msg, new Point(plot.X + 12, plot.Y + plot.Height / 2 - 6));
            return;
        }

        var minX = seriesList.SelectMany(s => s.Points).Min(p => p.Ticks);
        var maxX = seriesList.SelectMany(s => s.Points).Max(p => p.Ticks);
        var maxY = seriesList.SelectMany(s => s.Points).Where(p => p.Value >= 0).DefaultIfEmpty(new(0, 1)).Max(p => p.Value);
        if (maxY <= 0)
            maxY = 1;
        if (maxX <= minX)
            maxX = minX + 1;

        // Grid lines
        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#2E3943")), 1);
        for (var i = 1; i < 4; i++)
        {
            var y = plot.Y + plot.Height * i / 4.0;
            context.DrawLine(gridPen, new Point(plot.X, y), new Point(plot.Right, y));
        }

        foreach (var series in seriesList)
        {
            var color = Color.Parse(series.ColorHex);
            var pen = new Pen(new SolidColorBrush(color), 1.75);
            var points = series.Points
                .Where(p => p.Value >= 0)
                .OrderBy(p => p.Ticks)
                .ToList();
            if (points.Count < 2)
                continue;

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (var i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    var x = plot.X + (p.Ticks - minX) * plot.Width / (maxX - minX);
                    var y = plot.Bottom - Math.Clamp(p.Value / maxY, 0, 1) * plot.Height;
                    if (i == 0)
                        ctx.BeginFigure(new Point(x, y), false);
                    else
                        ctx.LineTo(new Point(x, y));
                }
            }

            context.DrawGeometry(null, pen, geo);
        }
    }
}
