using XenAdmin;

namespace XcpNgCenter.Shell.Services.Performance;

public readonly record struct RrdPoint(long Ticks, double Value);

public enum RrdArchiveInterval
{
    FiveSecond,
    OneMinute,
    OneHour,
    OneDay
}

public sealed class RrdSeries
{
    private const int NegativeValue = -1;

    public RrdSeries(string id, string dataSourceName, string friendlyName, string? units)
    {
        Id = id;
        DataSourceName = dataSourceName;
        FriendlyName = friendlyName;
        Units = units ?? "";
        MultiplyingFactor = ResolveFactor(units);
    }

    public string Id { get; }
    public string DataSourceName { get; }
    public string FriendlyName { get; }
    public string Units { get; }
    public int MultiplyingFactor { get; }
    public List<RrdPoint> Points { get; } = new();
    public bool Hide { get; set; }

    public static bool TryParseId(string id, out string objType, out string objUuid, out string dataSourceName)
    {
        var bits = id.Split(':').ToList();
        if (bits.Count > 3)
            bits.RemoveAt(0);

        if (bits.Count >= 3)
        {
            objType = bits[0];
            objUuid = bits[1];
            dataSourceName = bits[2];
            return true;
        }

        objType = objUuid = dataSourceName = "";
        return false;
    }

    public void AddRawValue(string raw, long currentTime, IReadOnlyList<RrdSeries> batch)
    {
        var value = XenAdmin.Core.Helpers.StringToDouble(raw);
        var bad = double.IsNaN(value) || double.IsInfinity(value);
        var y = bad ? NegativeValue : value * MultiplyingFactor;

        // Convert free→used for memory pairs (same as WinForms DataSet).
        if (DataSourceName is "memory_total_kib" or "memory")
        {
            var freeName = DataSourceName == "memory_total_kib" ? "memory_free_kib" : "memory_internal_free";
            var other = batch.FirstOrDefault(s => s.DataSourceName == freeName);
            if (other != null && other.Points.Count - 1 == Points.Count)
            {
                var otherY = other.Points[^1].Value;
                y = bad || otherY < 0 ? NegativeValue : y - otherY;
                other.Points[^1] = new RrdPoint(other.Points[^1].Ticks, y);
            }
        }
        else if (DataSourceName is "memory_free_kib" or "memory_internal_free")
        {
            var totalName = DataSourceName == "memory_free_kib" ? "memory_total_kib" : "memory";
            var other = batch.FirstOrDefault(s => s.DataSourceName == totalName);
            if (other != null && other.Points.Count - 1 == Points.Count)
            {
                var otherY = other.Points[^1].Value;
                y = bad || otherY < 0 ? NegativeValue : otherY - y;
            }
        }

        Points.Add(new RrdPoint(currentTime, y));
    }

    public void MergePoints(IEnumerable<RrdPoint> incoming, int maxPoints)
    {
        foreach (var p in incoming.OrderByDescending(p => p.Ticks))
        {
            if (Points.Any(existing => existing.Ticks == p.Ticks))
                continue;
            Points.Add(p);
        }

        Points.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
        if (Points.Count > maxPoints)
            Points.RemoveRange(maxPoints, Points.Count - maxPoints);
    }

    private static int ResolveFactor(string? units) => units switch
    {
        "(fraction)" => 100,
        "s" => (int)Util.DEC_GIGA,
        "ms" => (int)Util.DEC_MEGA,
        "μs" => (int)Util.DEC_KILO,
        "KiB" => (int)Util.BINARY_KILO,
        "MiB/s" => (int)Util.BINARY_MEGA,
        _ => 1
    };
}

public sealed class RrdArchive
{
    public RrdArchive(int maxPoints) => MaxPoints = maxPoints;

    public int MaxPoints { get; set; }
    public Dictionary<string, RrdSeries> SeriesById { get; } = new(StringComparer.Ordinal);

    public void Clear() => SeriesById.Clear();

    public void Merge(IEnumerable<RrdSeries> sets)
    {
        foreach (var set in sets)
        {
            if (set.Hide)
                continue;

            if (!SeriesById.TryGetValue(set.Id, out var existing))
            {
                existing = new RrdSeries(set.Id, set.DataSourceName, set.FriendlyName, set.Units)
                {
                    Hide = set.Hide
                };
                SeriesById[set.Id] = existing;
            }

            existing.MergePoints(set.Points, MaxPoints);
        }
    }
}
