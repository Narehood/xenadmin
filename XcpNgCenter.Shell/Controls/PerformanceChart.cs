using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using XcpNgCenter.Shell.Services.Performance;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Controls;

/// <summary>
/// Multi-series RRD chart with time-axis labels and pointer hover readouts.
/// </summary>
public sealed class PerformanceChart : Control
{
    public static readonly StyledProperty<IEnumerable<PerformanceSeriesView>?> SeriesProperty =
        AvaloniaProperty.Register<PerformanceChart, IEnumerable<PerformanceSeriesView>?>(nameof(Series));

    public static readonly StyledProperty<RrdArchiveInterval> IntervalProperty =
        AvaloniaProperty.Register<PerformanceChart, RrdArchiveInterval>(nameof(Interval), RrdArchiveInterval.FiveSecond);

    public static readonly StyledProperty<double?> YAxisMaxProperty =
        AvaloniaProperty.Register<PerformanceChart, double?>(nameof(YAxisMax));

    private Point? _pointer;
    private bool _pointerInside;
    private static FontFamily? _chartFontFamily;

    static PerformanceChart()
    {
        AffectsRender<PerformanceChart>(SeriesProperty, IntervalProperty, YAxisMaxProperty, BoundsProperty);
        ClipToBoundsProperty.OverrideDefaultValue<PerformanceChart>(true);
    }

    private static FontFamily ChartFontFamily => _chartFontFamily ??= ResolveChartFont();

    private static FontFamily ResolveChartFont()
    {
        try
        {
            if (Application.Current?.Resources.TryGetResource("Font.Outfit", null, out var resource) == true
                && resource is FontFamily family)
                return family;
        }
        catch
        {
            // Fall through to embedded family name.
        }

        return new FontFamily(
            "avares://XcpNgCenter.Shell/Assets/Fonts/Outfit-Regular.ttf#Outfit," +
            "avares://XcpNgCenter.Shell/Assets/Fonts/Outfit-SemiBold.ttf#Outfit," +
            "avares://XcpNgCenter.Shell/Assets/Fonts/Outfit-Bold.ttf#Outfit");
    }

    private static Typeface ChartTypeface => new(ChartFontFamily);

    public PerformanceChart()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public IEnumerable<PerformanceSeriesView>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public RrdArchiveInterval Interval
    {
        get => GetValue(IntervalProperty);
        set => SetValue(IntervalProperty, value);
    }

    /// <summary>Fixed Y-axis maximum (e.g. 100 for CPU %). Null = scale to data peak.</summary>
    public double? YAxisMax
    {
        get => GetValue(YAxisMaxProperty);
        set => SetValue(YAxisMaxProperty, value);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        _pointerInside = true;
        InvalidateVisual();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _pointerInside = true;
        _pointer = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerInside = false;
        _pointer = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width < 8 || bounds.Height < 8)
            return;

        const double padL = 10;
        const double padR = 10;
        const double padT = 10;
        const double padB = 28; // room for time labels
        var plot = new Rect(
            padL,
            padT,
            Math.Max(1, bounds.Width - padL - padR),
            Math.Max(1, bounds.Height - padT - padB));

        context.FillRectangle(new SolidColorBrush(Color.Parse("#1A222A")), plot, 4);

        var seriesList = Series?.Where(s => s.Points.Count > 1).ToList() ?? [];
        if (seriesList.Count == 0)
        {
            var msg = new FormattedText(
                "No samples yet",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                ChartTypeface,
                12,
                new SolidColorBrush(Color.Parse("#9AA6B2")));
            context.DrawText(msg, new Point(plot.X + 12, plot.Y + plot.Height / 2 - 6));
            return;
        }

        var minX = seriesList.SelectMany(s => s.Points).Min(p => p.Ticks);
        var maxX = seriesList.SelectMany(s => s.Points).Max(p => p.Ticks);
        var fixedMax = YAxisMax;
        var maxY = fixedMax is > 0
            ? fixedMax.Value
            : seriesList.SelectMany(s => s.Points).Where(p => p.Value >= 0).DefaultIfEmpty(new(0, 1)).Max(p => p.Value);
        if (maxY <= 0)
            maxY = 1;
        if (maxX <= minX)
            maxX = minX + 1;

        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#2E3943")), 1);
        for (var i = 1; i < 4; i++)
        {
            var y = plot.Y + plot.Height * i / 4.0;
            context.DrawLine(gridPen, new Point(plot.X, y), new Point(plot.Right, y));
        }

        if (fixedMax is > 0)
            DrawPercentYAxis(context, plot, maxY);

        DrawTimeAxis(context, plot, minX, maxX);

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
                    var x = MapX(plot, p.Ticks, minX, maxX);
                    var y = MapY(plot, p.Value, maxY);
                    if (i == 0)
                        ctx.BeginFigure(new Point(x, y), false);
                    else
                        ctx.LineTo(new Point(x, y));
                }
            }

            context.DrawGeometry(null, pen, geo);
        }

        if (_pointerInside && _pointer is { } pt && plot.Contains(pt))
            DrawHover(context, plot, seriesList, minX, maxX, maxY, pt);
    }

    private static void DrawPercentYAxis(DrawingContext context, Rect plot, double maxY)
    {
        var muted = new SolidColorBrush(Color.Parse("#9AA6B2"));
        for (var i = 0; i <= 4; i++)
        {
            var fraction = i / 4.0;
            var value = maxY * (1.0 - fraction);
            var y = plot.Y + plot.Height * fraction;
            var label = maxY <= 100 && Math.Abs(maxY - 100) < 0.01
                ? $"{value:0}%"
                : $"{value:0.##}";
            var text = new FormattedText(
                label,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                ChartTypeface,
                9,
                muted);
            var ty = y - text.Height / 2;
            if (i == 0)
                ty = plot.Y;
            else if (i == 4)
                ty = plot.Bottom - text.Height;
            context.DrawText(text, new Point(plot.X + 4, ty));
        }
    }

    private void DrawTimeAxis(DrawingContext context, Rect plot, long minX, long maxX)
    {
        var span = TimeSpan.FromTicks(Math.Max(1, maxX - minX));
        var (tickCount, format) = ResolveAxisStyle(Interval, span);
        var muted = new SolidColorBrush(Color.Parse("#9AA6B2"));
        var tickPen = new Pen(new SolidColorBrush(Color.Parse("#2E3943")), 1);

        for (var i = 0; i <= tickCount; i++)
        {
            var t = minX + (maxX - minX) * i / tickCount;
            var x = MapX(plot, t, minX, maxX);
            context.DrawLine(tickPen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 4));

            var label = new DateTime(t, DateTimeKind.Local).ToString(format);
            var text = new FormattedText(
                label,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                ChartTypeface,
                10,
                muted);

            var tx = x - text.Width / 2;
            if (i == 0)
                tx = plot.X;
            else if (i == tickCount)
                tx = plot.Right - text.Width;

            context.DrawText(text, new Point(tx, plot.Bottom + 6));
        }
    }

    private void DrawHover(
        DrawingContext context,
        Rect plot,
        List<PerformanceSeriesView> seriesList,
        long minX,
        long maxX,
        double maxY,
        Point pt)
    {
        var hoverTicks = minX + (long)((pt.X - plot.X) / plot.Width * (maxX - minX));
        var crossPen = new Pen(new SolidColorBrush(Color.Parse("#80F07318")), 1.25);
        context.DrawLine(crossPen, new Point(pt.X, plot.Y), new Point(pt.X, plot.Bottom));

        var lines = new List<(string Text, Color Color)>();
        var stamp = new DateTime(hoverTicks, DateTimeKind.Local);
        lines.Add((FormatHoverTime(stamp), Color.Parse("#F2F4F6")));

        foreach (var series in seriesList)
        {
            var points = series.Points.Where(p => p.Value >= 0).OrderBy(p => p.Ticks).ToList();
            if (points.Count == 0)
                continue;

            var sample = Nearest(points, hoverTicks);
            var color = Color.Parse(series.ColorHex);
            var x = MapX(plot, sample.Ticks, minX, maxX);
            var y = MapY(plot, sample.Value, maxY);
            context.DrawEllipse(new SolidColorBrush(color), null, new Point(x, y), 3.5, 3.5);
            lines.Add(($"{series.Name}: {FormatValue(sample.Value)}", color));
        }

        DrawTooltip(context, plot, pt, lines);
    }

    private static void DrawTooltip(
        DrawingContext context,
        Rect plot,
        Point pt,
        List<(string Text, Color Color)> lines)
    {
        if (lines.Count == 0)
            return;

        const double pad = 8;
        const double lineH = 16;
        var texts = lines.Select(l => new FormattedText(
            l.Text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            ChartTypeface,
            11,
            new SolidColorBrush(l.Color))).ToList();

        var width = texts.Max(t => t.Width) + pad * 2;
        var height = texts.Count * lineH + pad * 2 - 2;

        var boxX = pt.X + 14;
        var boxY = pt.Y - height - 8;
        if (boxX + width > plot.Right)
            boxX = pt.X - width - 14;
        if (boxY < plot.Y)
            boxY = pt.Y + 14;
        if (boxX < plot.X)
            boxX = plot.X + 4;
        if (boxY + height > plot.Bottom)
            boxY = plot.Bottom - height - 4;

        var box = new Rect(boxX, boxY, width, height);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#F0161C22")), box, 6);
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#F07318")), 1), box, 6);

        for (var i = 0; i < texts.Count; i++)
            context.DrawText(texts[i], new Point(boxX + pad, boxY + pad + i * lineH - 1));
    }

    private static (int TickCount, string Format) ResolveAxisStyle(RrdArchiveInterval interval, TimeSpan span)
        => interval switch
        {
            RrdArchiveInterval.FiveSecond => (5, span.TotalMinutes <= 15 ? "HH:mm:ss" : "HH:mm"),
            RrdArchiveInterval.OneMinute => (6, "HH:mm"),
            RrdArchiveInterval.OneHour => (7, "MMM d HH:mm"),
            RrdArchiveInterval.OneDay => (6, "MMM d"),
            _ => (5, "g")
        };

    private static string FormatHoverTime(DateTime local)
        => local.ToString("g");

    private static RrdPoint Nearest(IReadOnlyList<RrdPoint> points, long ticks)
    {
        var best = points[0];
        var bestDist = Math.Abs(best.Ticks - ticks);
        for (var i = 1; i < points.Count; i++)
        {
            var d = Math.Abs(points[i].Ticks - ticks);
            if (d < bestDist)
            {
                best = points[i];
                bestDist = d;
            }
        }

        return best;
    }

    private static double MapX(Rect plot, long ticks, long minX, long maxX)
        => plot.X + (ticks - minX) * plot.Width / (maxX - minX);

    private static double MapY(Rect plot, double value, double maxY)
        => plot.Bottom - Math.Clamp(value / maxY, 0, 1) * plot.Height;

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
