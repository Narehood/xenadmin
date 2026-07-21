using System.Globalization;
using System.Xml;
using XenAdmin;
using XenAdmin.Alerts;
using XenAdmin.Core;
using XenAPI;

namespace XcpNgCenter.Shell.Alerts;

/// <summary>
/// Performance-alarm message formatting without WinForms dialogs / fix links.
/// </summary>
public sealed class ShellAlarmMessageAlert : ShellMessageAlert
{
    private static readonly log4net.ILog Log =
        log4net.LogManager.GetLogger(typeof(ShellAlarmMessageAlert));

    private enum AlarmType
    {
        None,
        Cpu,
        Net,
        Disk,
        FileSystem,
        Memory,
        Storage,
        Dom0MemoryDemand,
        LogFileSystem,
        SrPhysicalUtilisation
    }

    private AlarmType _alarmType;
    private double _currentValue;
    private double _triggerLevel;
    private int _triggerPeriod;
    private SR? _sr;

    public ShellAlarmMessageAlert(Message m)
        : base(m)
    {
        ParseAlarmMessage(m);
    }

    private void ParseAlarmMessage(Message m)
    {
        var lines = new List<string>(m.body.Split('\n'));
        if (lines.Count < 2)
            return;

        var value = lines[0].Replace("value: ", "");
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _currentValue);

        var variableName = "";
        try
        {
            var xml = string.Join("", lines.GetRange(1, lines.Count - 1).ToArray()).Replace("config:", "");
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var name = doc.GetElementsByTagName("name");
            if (name.Count > 0)
                variableName = name[0]!.Attributes!["value"]!.Value;

            var level = doc.GetElementsByTagName("alarm_trigger_level");
            if (level.Count > 0)
                double.TryParse(level[0]!.Attributes!["value"]!.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out _triggerLevel);

            var period = doc.GetElementsByTagName("alarm_trigger_period");
            if (period.Count > 0)
                int.TryParse(period[0]!.Attributes!["value"]!.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out _triggerPeriod);
        }
        catch (Exception e)
        {
            Log.Debug("Error parsing alarm message description", e);
        }

        _alarmType = variableName switch
        {
            PerfmonDefinition.ALARM_TYPE_CPU => AlarmType.Cpu,
            PerfmonDefinition.ALARM_TYPE_NETWORK => AlarmType.Net,
            PerfmonDefinition.ALARM_TYPE_DISK => AlarmType.Disk,
            PerfmonDefinition.ALARM_TYPE_FILESYSTEM => AlarmType.FileSystem,
            PerfmonDefinition.ALARM_TYPE_MEMORY_FREE => AlarmType.Memory,
            PerfmonDefinition.ALARM_TYPE_MEMORY_DOM0_USAGE => AlarmType.Dom0MemoryDemand,
            PerfmonDefinition.ALARM_TYPE_LOG_FILESYSTEM => AlarmType.LogFileSystem,
            PerfmonDefinition.ALARM_TYPE_SR_PHYSICAL_UTILISATION => AlarmType.SrPhysicalUtilisation,
            _ => ResolveStorageAlarm(variableName)
        };
    }

    private AlarmType ResolveStorageAlarm(string variableName)
    {
        var match = PerfmonDefinition.SrRegex.Match(variableName);
        if (!match.Success)
            return AlarmType.None;

        _sr = GetStorage(match.Groups[1].Value);
        return AlarmType.Storage;
    }

    private SR? GetStorage(string shortUuid)
    {
        if (Connection == null)
            return null;
        return Connection.Cache.SRs.FirstOrDefault(sr =>
            sr.uuid.StartsWith(shortUuid, StringComparison.OrdinalIgnoreCase));
    }

    public override AlertPriority Priority => _alarmType switch
    {
        AlarmType.FileSystem => AlertPriority.Priority2,
        AlarmType.LogFileSystem => AlertPriority.Priority3,
        _ => base.Priority
    };

    public override string Description => _alarmType switch
    {
        AlarmType.Cpu => string.Format(Messages.ALERT_ALARM_CPU_DESCRIPTION,
            Helpers.GetNameAndObject(XenObject),
            Util.PercentageString(_currentValue),
            Util.TimeString(_triggerPeriod),
            Util.PercentageString(_triggerLevel)),
        AlarmType.Net => string.Format(Messages.ALERT_ALARM_NETWORK_DESCRIPTION,
            Helpers.GetNameAndObject(XenObject),
            Util.DataRateString(_currentValue),
            Util.TimeString(_triggerPeriod),
            Util.DataRateString(_triggerLevel)),
        AlarmType.Disk => string.Format(Messages.ALERT_ALARM_DISK_DESCRIPTION,
            Helpers.GetNameAndObject(XenObject),
            Util.DataRateString(_currentValue),
            Util.TimeString(_triggerPeriod),
            Util.DataRateString(_triggerLevel)),
        AlarmType.FileSystem => string.Format(Messages.ALERT_ALARM_FILESYSTEM_DESCRIPTION,
            Helpers.GetNameAndObject(XenObject),
            Util.PercentageString(_currentValue), BrandManager.ProductBrand),
        AlarmType.Memory => string.Format(Messages.ALERT_ALARM_MEMORY_DESCRIPTION,
            Helpers.GetNameAndObject(XenObject),
            Util.MemorySizeStringSuitableUnits(_currentValue * Util.BINARY_KILO, false),
            Util.TimeString(_triggerPeriod),
            Util.MemorySizeStringSuitableUnits(_triggerLevel * Util.BINARY_KILO, false)),
        AlarmType.Dom0MemoryDemand => string.Format(Messages.ALERT_ALARM_DOM0_MEMORY_DEMAND_DESCRIPTION,
            Helpers.GetNameAndObject(XenObject),
            Util.PercentageString(_currentValue),
            Util.PercentageString(_triggerLevel)),
        AlarmType.Storage => string.Format(Messages.ALERT_ALARM_STORAGE_DESCRIPTION,
            Helpers.GetNameAndObject(XenObject),
            _sr?.Name() ?? "",
            Util.DataRateString(_currentValue * Util.BINARY_MEGA),
            Util.TimeString(_triggerPeriod),
            Util.DataRateString(_triggerLevel * Util.BINARY_MEGA)),
        AlarmType.LogFileSystem => string.Format(Messages.ALERT_ALARM_LOG_FILESYSTEM_DESCRIPTION,
            Helpers.GetNameAndObject(XenObject),
            Util.PercentageString(_currentValue)),
        AlarmType.SrPhysicalUtilisation => string.Format(Messages.ALERT_ALARM_SR_PHYSICAL_UTILISATION_DESCRIPTION,
            Helpers.GetNameAndObject(XenObject),
            Util.PercentageString(_currentValue),
            Util.PercentageString(_triggerLevel)),
        _ => base.Description
    };

    public override string Title => _alarmType switch
    {
        AlarmType.Cpu => Messages.ALERT_ALARM_CPU,
        AlarmType.Net => Messages.ALERT_ALARM_NETWORK,
        AlarmType.Disk => Messages.ALERT_ALARM_DISK,
        AlarmType.FileSystem => Messages.ALERT_ALARM_FILESYSTEM,
        AlarmType.Memory => Messages.ALERT_ALARM_MEMORY,
        AlarmType.Storage => Messages.ALERT_ALARM_STORAGE,
        AlarmType.Dom0MemoryDemand => Messages.ALERT_ALARM_DOM0_MEMORY,
        AlarmType.LogFileSystem => Messages.ALERT_ALARM_LOG_FILESYSTEM,
        AlarmType.SrPhysicalUtilisation => Messages.ALERT_ALARM_SR_PHYSICAL_UTILISATION,
        _ => base.Title
    };
}
