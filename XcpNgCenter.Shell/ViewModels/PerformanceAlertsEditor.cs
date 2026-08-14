using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using XenAdmin.Alerts;
using XenAdmin.Core;
using XenAPI;

namespace XcpNgCenter.Shell.ViewModels;

public partial class PerformanceAlertRule : ViewModelBase
{
    private readonly Func<decimal, decimal> _toXapiThreshold;
    private readonly decimal _initialThreshold;
    private readonly decimal _initialDurationMinutes;
    private readonly bool _initialEnabled;

    public PerformanceAlertRule(
        string definitionName,
        string title,
        string description,
        string unit,
        decimal defaultThreshold,
        decimal minimumThreshold,
        decimal maximumThreshold,
        decimal thresholdIncrement,
        Func<decimal, decimal> toGuiThreshold,
        Func<decimal, decimal> toXapiThreshold,
        PerfmonDefinition? definition)
    {
        DefinitionName = definitionName;
        Title = title;
        Description = description;
        Unit = unit;
        MinimumThreshold = minimumThreshold;
        MaximumThreshold = maximumThreshold;
        ThresholdIncrement = thresholdIncrement;
        _toXapiThreshold = toXapiThreshold;

        Enabled = definition?.HasValueSet == true;
        Threshold = definition == null
            ? defaultThreshold
            : toGuiThreshold(definition.AlarmTriggerLevel);
        DurationMinutes = definition == null
            ? 1m
            : definition.AlarmTriggerPeriod / 60m;

        _initialEnabled = Enabled;
        _initialThreshold = Threshold;
        _initialDurationMinutes = DurationMinutes;
    }

    public string DefinitionName { get; }
    public string Title { get; }
    public string Description { get; }
    public string Unit { get; }
    public decimal MinimumThreshold { get; }
    public decimal MaximumThreshold { get; }
    public decimal ThresholdIncrement { get; }

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private decimal _threshold;

    [ObservableProperty]
    private decimal _durationMinutes;

    public bool HasChanges =>
        Enabled != _initialEnabled
        || Threshold != _initialThreshold
        || DurationMinutes != _initialDurationMinutes;

    public bool TryValidate(out string error)
    {
        if (!Enabled)
        {
            error = string.Empty;
            return true;
        }

        if (Threshold < MinimumThreshold || Threshold > MaximumThreshold)
        {
            error = $"{Title} must be between {MinimumThreshold:g} and {MaximumThreshold:g} {Unit}.";
            return false;
        }

        if (DurationMinutes < 1m || DurationMinutes > 60m)
        {
            error = $"{Title} duration must be between 1 and 60 minutes.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public PerfmonDefinition BuildDefinition(decimal alertIntervalMinutes) =>
        new(
            DefinitionName,
            _toXapiThreshold(Threshold),
            DurationMinutes * 60m,
            alertIntervalMinutes * 60m);
}

public partial class PerformanceAlertsEditor : ViewModelBase
{
    private const decimal MaximumThroughputKib = 2147483647m;
    private readonly decimal _initialAlertIntervalMinutes;

    public PerformanceAlertsEditor(IXenObject xenObject)
    {
        var definitions = PerfmonDefinition.GetPerfmonDefinitions(xenObject);
        var dom0Definitions = xenObject is Host host
            ? PerfmonDefinition.GetPerfmonDefinitions(host.ControlDomainZero()).ToList()
            : [];

        if (xenObject is VM)
        {
            Rules.Add(CreateCpuRule(Find(definitions, definition => definition.IsCPUUsage)));
            Rules.Add(CreateNetworkRule(Find(definitions, definition => definition.IsNetworkUsage)));
            Rules.Add(CreateDiskRule(Find(definitions, definition => definition.IsDiskUsage)));
        }
        else if (xenObject is Host)
        {
            Rules.Add(CreateCpuRule(Find(definitions, definition => definition.IsCPUUsage)));
            Rules.Add(CreateNetworkRule(Find(definitions, definition => definition.IsNetworkUsage)));
            Rules.Add(CreateHostMemoryRule(Find(definitions, definition => definition.IsMemoryUsage)));
            Rules.Add(CreateDom0MemoryRule(Find(dom0Definitions, definition => definition.IsDom0MemoryUsage)));
        }

        var configuredDefinition = Rules
            .Where(rule => rule.Enabled)
            .Select(rule => FindDefinition(definitions, dom0Definitions, rule.DefinitionName))
            .FirstOrDefault(definition => definition != null);
        AlertIntervalMinutes = configuredDefinition?.AlarmAutoInhibitPeriod / 60m ?? 60m;
        _initialAlertIntervalMinutes = AlertIntervalMinutes;
    }

    public ObservableCollection<PerformanceAlertRule> Rules { get; } = new();

    [ObservableProperty]
    private decimal _alertIntervalMinutes = 60m;

    public bool HasChanges =>
        Rules.Any(rule => rule.HasChanges)
        || (Rules.Any(rule => rule.Enabled) && AlertIntervalMinutes != _initialAlertIntervalMinutes);

    public bool TryValidate(out string error)
    {
        if (AlertIntervalMinutes < 5m || AlertIntervalMinutes > 86400m
            || AlertIntervalMinutes % 5m != 0m)
        {
            error = "The alert repeat interval must be between 5 and 86,400 minutes and a multiple of 5.";
            return false;
        }

        foreach (var rule in Rules)
        {
            if (!rule.TryValidate(out error))
                return false;
        }

        error = string.Empty;
        return true;
    }

    public List<PerfmonDefinition> BuildDefinitions() =>
        Rules
            .Where(rule => rule.Enabled)
            .Select(rule => rule.BuildDefinition(AlertIntervalMinutes))
            .ToList();

    private static PerformanceAlertRule CreateCpuRule(PerfmonDefinition? definition) =>
        new(
            PerfmonDefinition.ALARM_TYPE_CPU,
            "CPU usage",
            "Alert when average CPU usage remains above the threshold.",
            "%",
            50m,
            5m,
            100m,
            5m,
            value => value * 100m,
            value => value / 100m,
            definition);

    private static PerformanceAlertRule CreateNetworkRule(PerfmonDefinition? definition) =>
        new(
            PerfmonDefinition.ALARM_TYPE_NETWORK,
            "Network usage",
            "Alert when aggregate network throughput remains above the threshold.",
            "KiB/s",
            100m,
            1m,
            MaximumThroughputKib,
            1m,
            value => value / 1024m,
            value => value * 1024m,
            definition);

    private static PerformanceAlertRule CreateDiskRule(PerfmonDefinition? definition) =>
        new(
            PerfmonDefinition.ALARM_TYPE_DISK,
            "Disk usage",
            "Alert when aggregate virtual-disk throughput remains above the threshold.",
            "KiB/s",
            1000m,
            1m,
            MaximumThroughputKib,
            5m,
            value => value / 1024m,
            value => value * 1024m,
            definition);

    private static PerformanceAlertRule CreateHostMemoryRule(PerfmonDefinition? definition) =>
        new(
            PerfmonDefinition.ALARM_TYPE_MEMORY_FREE,
            "Free host memory",
            "Alert when available host memory remains below the threshold.",
            "MiB",
            1000m,
            1m,
            MaximumThroughputKib,
            5m,
            value => value / 1024m,
            value => value * 1024m,
            definition);

    private static PerformanceAlertRule CreateDom0MemoryRule(PerfmonDefinition? definition) =>
        new(
            PerfmonDefinition.ALARM_TYPE_MEMORY_DOM0_USAGE,
            "Control-domain memory usage",
            "Alert when control-domain memory usage remains above the threshold.",
            "%",
            95m,
            5m,
            100m,
            5m,
            value => value * 100m,
            value => value / 100m,
            definition);

    private static PerfmonDefinition? Find(
        IEnumerable<PerfmonDefinition> definitions,
        Func<PerfmonDefinition, bool> predicate) =>
        definitions.FirstOrDefault(predicate);

    private static PerfmonDefinition? FindDefinition(
        IEnumerable<PerfmonDefinition> definitions,
        IEnumerable<PerfmonDefinition> dom0Definitions,
        string definitionName)
    {
        var source = string.Equals(
            definitionName,
            PerfmonDefinition.ALARM_TYPE_MEMORY_DOM0_USAGE,
            StringComparison.Ordinal)
            ? dom0Definitions
            : definitions;

        return definitionName switch
        {
            PerfmonDefinition.ALARM_TYPE_CPU => source.FirstOrDefault(definition => definition.IsCPUUsage),
            PerfmonDefinition.ALARM_TYPE_NETWORK => source.FirstOrDefault(definition => definition.IsNetworkUsage),
            PerfmonDefinition.ALARM_TYPE_DISK => source.FirstOrDefault(definition => definition.IsDiskUsage),
            PerfmonDefinition.ALARM_TYPE_MEMORY_FREE => source.FirstOrDefault(definition => definition.IsMemoryUsage),
            PerfmonDefinition.ALARM_TYPE_MEMORY_DOM0_USAGE => source.FirstOrDefault(definition => definition.IsDom0MemoryUsage),
            _ => null
        };
    }
}
