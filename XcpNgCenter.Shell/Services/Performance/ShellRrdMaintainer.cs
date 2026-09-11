using System.Globalization;
using System.Xml;
using Avalonia.Threading;
using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;

namespace XcpNgCenter.Shell.Services.Performance;

/// <summary>
/// WinForms-free RRD poller: full dump + incremental <c>/rrd_updates</c> into in-memory archives.
/// </summary>
public sealed class ShellRrdMaintainer : IDisposable
{
    private static readonly log4net.ILog Log =
        log4net.LogManager.GetLogger(typeof(ShellRrdMaintainer));

    private const long TicksInOneSecond = 10_000_000;
    private const int FiveSecondsInTenMinutes = 120;
    private const int MinutesInTwoHours = 120;
    private const int HoursInOneWeek = 168;
    private const int DaysInOneYear = 366;
    private const int SleepMs = 5000;

    private readonly object _gate = new();
    private volatile bool _cancel;
    private volatile bool _running;
    private List<Data_source> _dataSources = new();
    private List<RrdSeries>? _setsAdded;
    private long _endTime;
    private bool _bailOut;
    private long _currentInterval;
    private long _stepSize;
    private long _currentTime;
    private int _valueCount;
    private string _lastNode = "";
    private readonly Dictionary<RrdArchiveInterval, DateTime> _lastPoll = new();
    private readonly Dictionary<RrdArchiveInterval, long> _lastSample = new();
    private readonly Action<Action> _dispatch;

    public ShellRrdMaintainer(IXenObject xenObject) : this(xenObject, action =>
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    })
    {
    }

    internal ShellRrdMaintainer(IXenObject xenObject, Action<Action> dispatch)
    {
        XenObject = xenObject;
        _dispatch = dispatch;
        Archives[RrdArchiveInterval.FiveSecond] = new RrdArchive(FiveSecondsInTenMinutes + 4);
        Archives[RrdArchiveInterval.OneMinute] = new RrdArchive(MinutesInTwoHours);
        Archives[RrdArchiveInterval.OneHour] = new RrdArchive(HoursInOneWeek);
        Archives[RrdArchiveInterval.OneDay] = new RrdArchive(DaysInOneYear);
    }

    public IXenObject XenObject { get; }

    public Dictionary<RrdArchiveInterval, RrdArchive> Archives { get; } = new();

    public bool LoadingInitialData { get; private set; }

    public event Action? ArchivesUpdated;

    private TimeSpan ClientServerOffset => XenObject.Connection?.ServerTimeOffset ?? TimeSpan.Zero;

    private DateTime ServerNow => DateTime.UtcNow.Subtract(ClientServerOffset);

    public void Start()
    {
        lock (_gate)
        {
            if (_running || _cancel)
                return;
            _running = true;
        }

        ThreadPool.QueueUserWorkItem(_ => RunLoop());
    }

    public void Dispose()
    {
        _cancel = true;
    }

    private void RunLoop()
    {
        try
        {
            var serverWas = ServerNow;
            InitialLoad(serverWas);

            while (!_cancel)
            {
                serverWas = ServerNow;
                foreach (var interval in Enum.GetValues<RrdArchiveInterval>())
                {
                    if (_cancel)
                        break;
                    if (!_lastPoll.TryGetValue(interval, out var last)
                        || serverWas - last >= TimeSpan.FromSeconds(IntervalSeconds(interval)))
                    {
                        PollArchive(interval, serverWas, (start, seconds) =>
                            Get(xo => UpdateUri(xo, start, seconds), RrdUpdateInspect, XenObject)
                                ? _setsAdded : null);
                    }
                }

                RaiseUpdated();
                Thread.Sleep(SleepMs);
            }
        }
        finally
        {
            lock (_gate)
                _running = false;
        }
    }

    private void InitialLoad(DateTime initialServerTime)
    {
        if (Helpers.FeatureForbidden(XenObject, Host.RestrictPerformanceGraphs))
        {
            Archives[RrdArchiveInterval.OneHour].MaxPoints = 24;
            Archives[RrdArchiveInterval.OneDay].MaxPoints = 0;
        }

        foreach (var a in Archives.Values)
            a.Clear();

        if (_cancel)
            return;

        LoadingInitialData = true;
        RaiseUpdated();

        try
        {
            switch (XenObject)
            {
                case Host h:
                    _dataSources = Host.get_data_sources(h.Connection.Session, h.opaque_ref);
                    break;
                case VM vm when vm.power_state == vm_power_state.Running:
                    _dataSources = VM.get_data_sources(vm.Connection.Session, vm.opaque_ref);
                    break;
            }

            if (_cancel)
                return;

            Get(RrdsUri, RrdFullInspect, XenObject);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to retrieve data sources for '{XenObject.Name()}'", e);
        }

        if (_cancel)
            return;

        LoadingInitialData = false;
        foreach (var interval in Enum.GetValues<RrdArchiveInterval>())
            _lastPoll[interval] = initialServerTime;
        RaiseUpdated();
    }

    internal static int IntervalSeconds(RrdArchiveInterval interval) => interval switch
    {
        RrdArchiveInterval.FiveSecond => 5,
        RrdArchiveInterval.OneMinute => 60,
        RrdArchiveInterval.OneHour => 3600,
        RrdArchiveInterval.OneDay => 86400,
        _ => throw new ArgumentOutOfRangeException(nameof(interval))
    };

    internal void PollArchive(RrdArchiveInterval interval, DateTime serverNow,
        Func<long, int, List<RrdSeries>?> fetch)
    {
        _lastPoll[interval] = serverNow;
        var seconds = IntervalSeconds(interval);
        var maxPoints = Archives[interval].MaxPoints;
        if (_cancel || maxPoints == 0)
            return;

        var oldestRetained = new DateTimeOffset(serverNow).ToUnixTimeSeconds() - (long)maxPoints * seconds;
        var start = _lastSample.TryGetValue(interval, out var latest)
            ? Math.Max(oldestRetained, latest) : oldestRetained;
        var sets = fetch(start, seconds);
        if (_cancel || sets == null)
            return;
        RecordLatestSample(interval, sets);
        MergeOnUi(interval, sets);
    }

    private void RecordLatestSample(RrdArchiveInterval interval, List<RrdSeries> sets)
    {
        var ticks = sets.SelectMany(s => s.Points).Select(p => p.Ticks).DefaultIfEmpty(0).Max();
        if (ticks == 0)
            return;
        // RRD points are stored in local display time; requests use server Unix time.
        var latest = new DateTimeOffset(new DateTime(ticks, DateTimeKind.Local)).ToUnixTimeSeconds();
        if (!_lastSample.TryGetValue(interval, out var previous) || latest > previous)
            _lastSample[interval] = latest;
    }

    private bool Get(Func<IXenObject, Uri?> uriBuilder, Action<XmlReader, IXenObject> readerMethod, IXenObject xo)
    {
        _setsAdded = null;
        try
        {
            var uri = uriBuilder(xo);
            if (uri == null)
                return false;

            using var stream = HTTPHelper.GET(uri, xo.Connection, true);
            using var reader = XmlReader.Create(stream);
            _setsAdded = new List<RrdSeries>();
            while (reader.Read() && !_cancel)
                readerMethod(reader, xo);
            return !_cancel;
        }
        catch (Exception e)
        {
            Log.Warn($"RRD get for {xo.Name()} failed", e);
            return false;
        }
    }

    private Uri? UpdateUri(IXenObject xo, long start, int seconds)
    {
        var sessionRef = xo.Connection?.Session?.opaque_ref;
        if (sessionRef == null)
            return null;

        var escaped = Uri.EscapeDataString(sessionRef);
        return xo switch
        {
            Host host => BuildUri(host, "rrd_updates",
                $"session_id={escaped}&start={start}&cf=AVERAGE&interval={seconds}&host=true"),
            VM vm => BuildUri(
                vm.Connection.Resolve(vm.resident_on) ?? Helpers.GetCoordinator(vm.Connection),
                "rrd_updates",
                $"session_id={escaped}&start={start}&cf=AVERAGE&interval={seconds}&vm_uuid={vm.uuid}"),
            _ => null
        };
    }

    private static Uri? RrdsUri(IXenObject xo)
    {
        var sessionRef = xo.Connection?.Session?.opaque_ref;
        if (sessionRef == null)
            return null;

        var escaped = Uri.EscapeDataString(sessionRef);
        return xo switch
        {
            Host host => BuildUri(host, "host_rrds", $"session_id={escaped}"),
            VM vm => BuildUri(
                vm.Connection.Resolve(vm.resident_on) ?? Helpers.GetCoordinator(vm.Connection),
                "vm_rrds",
                $"session_id={escaped}&uuid={vm.uuid}"),
            _ => null
        };
    }

    private static Uri BuildUri(Host host, string path, string query)
    {
        return new UriBuilder
        {
            Scheme = host.Connection.UriScheme,
            Host = host.address,
            Port = host.Connection.Port,
            Path = path,
            Query = query
        }.Uri;
    }

    private void RrdFullInspect(XmlReader reader, IXenObject xmo)
    {
        switch (reader.NodeType)
        {
            case XmlNodeType.Element:
                _lastNode = reader.Name;
                if (_lastNode == "row")
                {
                    _currentTime += _currentInterval * _stepSize * TicksInOneSecond;
                    _valueCount = 0;
                }
                break;
            case XmlNodeType.EndElement:
                _lastNode = reader.Name;
                if (_lastNode == "rra")
                {
                    if (_bailOut)
                    {
                        _bailOut = false;
                        return;
                    }

                    var interval = IntervalFromFiveSecs(_currentInterval);
                    if (interval != null && _setsAdded != null)
                    {
                        RecordLatestSample(interval.Value, _setsAdded);
                        MergeOnUi(interval.Value, CloneSets(_setsAdded));
                    }

                    if (_setsAdded != null)
                    {
                        foreach (var set in _setsAdded)
                            set.Points.Clear();
                    }

                    _bailOut = false;
                }
                break;
        }

        if (reader.NodeType != XmlNodeType.Text || _setsAdded == null)
            return;

        switch (_lastNode)
        {
            case "name":
            {
                var name = reader.ReadContentAsString();
                _setsAdded.Add(CreateSeries(xmo, name, hideForeign: false));
                break;
            }
            case "step":
                _stepSize = long.Parse(reader.ReadContentAsString(), CultureInfo.InvariantCulture);
                break;
            case "lastupdate":
                _endTime = long.Parse(reader.ReadContentAsString(), CultureInfo.InvariantCulture);
                break;
            case "pdp_per_row":
            {
                _currentInterval = long.Parse(reader.ReadContentAsString(), CultureInfo.InvariantCulture);
                var modInterval = _endTime % (_stepSize * _currentInterval);
                long stepCount = _currentInterval switch
                {
                    1 => FiveSecondsInTenMinutes,
                    12 => MinutesInTwoHours,
                    720 => HoursInOneWeek,
                    _ => DaysInOneYear
                };
                _currentTime = new DateTime(
                        (_endTime - modInterval - _stepSize * _currentInterval * stepCount) * TimeSpan.TicksPerSecond
                        + Util.TicksBefore1970)
                    .ToLocalTime().Ticks;
                break;
            }
            case "cf":
                if (reader.ReadContentAsString() != "AVERAGE")
                    _bailOut = true;
                break;
            case "v" when _bailOut || _setsAdded.Count <= _valueCount:
                break;
            case "v":
            {
                var set = _setsAdded[_valueCount];
                set.AddRawValue(reader.ReadContentAsString(), _currentTime);
                _valueCount++;
                break;
            }
        }
    }

    private void RrdUpdateInspect(XmlReader reader, IXenObject xo)
    {
        if (reader.NodeType == XmlNodeType.Element)
        {
            _lastNode = reader.Name;
            if (_lastNode == "row")
                _valueCount = 0;
        }

        if (reader.NodeType != XmlNodeType.Text || _setsAdded == null)
            return;

        if (_lastNode == "entry")
        {
            var str = reader.ReadContentAsString();
            RrdSeries? set = null;
            if (RrdSeries.TryParseId(str, out var objType, out var objUuid, out var dsName))
            {
                if (objType == "host")
                {
                    var host = xo.Connection.Cache.Hosts.FirstOrDefault(h => h.uuid == objUuid);
                    if (host != null)
                        set = CreateSeries(host, dsName, hideForeign: (xo as Host)?.uuid != objUuid);
                }
                else if (objType == "vm")
                {
                    var vm = xo.Connection.Cache.VMs.FirstOrDefault(v => v.uuid == objUuid);
                    if (vm != null)
                        set = CreateSeries(vm, dsName, hideForeign: (xo as VM)?.uuid != objUuid);
                }
            }

            set ??= new RrdSeries(str, str, str, null) { Hide = true };
            _setsAdded.Add(set);
        }
        else if (_lastNode == "t")
        {
            _currentTime = new DateTime(Convert.ToInt64(reader.ReadContentAsString()) * TimeSpan.TicksPerSecond + Util.TicksBefore1970)
                .ToLocalTime().Ticks;
        }
        else if (_lastNode == "v")
        {
            if (_setsAdded.Count <= _valueCount)
                return;
            var set = _setsAdded[_valueCount];
            set.AddRawValue(reader.ReadContentAsString(), _currentTime);
            _valueCount++;
        }
    }

    internal RrdSeries CreateSeries(IXenObject xo, string dataSourceName, bool hideForeign)
    {
        var id = xo switch
        {
            Host h => $"host:{h.uuid}:{dataSourceName}",
            VM vm => $"vm:{vm.uuid}:{dataSourceName}",
            _ => dataSourceName
        };

        var units = _dataSources.FirstOrDefault(d => d.name_label == dataSourceName)?.units;
        var friendly = Helpers.GetFriendlyDataSourceName(dataSourceName, xo);

        return new RrdSeries(id, dataSourceName, friendly, units)
        {
            Hide = hideForeign
        };
    }

    private static List<RrdSeries> CloneSets(List<RrdSeries> source)
    {
        var copy = new List<RrdSeries>(source.Count);
        foreach (var s in source)
        {
            var c = new RrdSeries(s.Id, s.DataSourceName, s.FriendlyName, s.Units) { Hide = s.Hide };
            c.Points.AddRange(s.Points);
            copy.Add(c);
        }
        return copy;
    }

    private static RrdArchiveInterval? IntervalFromFiveSecs(long currentInterval) => currentInterval switch
    {
        1 => RrdArchiveInterval.FiveSecond,
        12 => RrdArchiveInterval.OneMinute,
        720 => RrdArchiveInterval.OneHour,
        17280 => RrdArchiveInterval.OneDay,
        _ => null
    };

    private void MergeOnUi(RrdArchiveInterval interval, List<RrdSeries> sets)
    {
        void Apply()
        {
            if (!_cancel && Archives.TryGetValue(interval, out var archive))
                archive.Merge(sets);
        }

        _dispatch(Apply);
    }

    private void RaiseUpdated()
    {
        _dispatch(() => { if (!_cancel) ArchivesUpdated?.Invoke(); });
    }
}
