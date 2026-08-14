using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions;
using XenAdmin.Actions.NRPE;
using XenAPI;

namespace XcpNgCenter.Shell.ViewModels;

public enum NrpeThresholdKind
{
    Increasing,
    Decreasing,
    LoadTriple
}

public partial class NrpeCheckRow : ViewModelBase
{
    private readonly string _initialWarning;
    private readonly string _initialCritical;

    public NrpeCheckRow(
        string name,
        string label,
        NrpeThresholdKind kind,
        string warning,
        string critical)
    {
        Name = name;
        Label = label;
        Kind = kind;
        Warning = warning;
        Critical = critical;
        _initialWarning = warning;
        _initialCritical = critical;
    }

    public string Name { get; }
    public string Label { get; }
    public NrpeThresholdKind Kind { get; }
    public string Hint => Kind switch
    {
        NrpeThresholdKind.Decreasing => "Warning must be greater than critical.",
        NrpeThresholdKind.LoadTriple => "Three comma-separated load values.",
        _ => "Warning must be less than critical."
    };

    [ObservableProperty]
    private string _warning = string.Empty;

    [ObservableProperty]
    private string _critical = string.Empty;

    public bool HasChanges =>
        !string.Equals(Warning, _initialWarning, StringComparison.Ordinal)
        || !string.Equals(Critical, _initialCritical, StringComparison.Ordinal);

    public bool TryValidate(out string error)
    {
        if (Kind == NrpeThresholdKind.LoadTriple)
            return TryValidateLoadTriple(out error);

        if (!TryParseThreshold(Warning, out var warning)
            || !TryParseThreshold(Critical, out var critical))
        {
            error = $"{Label} thresholds must be numbers from 0.01 to 100.";
            return false;
        }

        var ordered = Kind == NrpeThresholdKind.Decreasing
            ? warning > critical
            : warning < critical;
        if (!ordered)
        {
            error = Kind == NrpeThresholdKind.Decreasing
                ? $"{Label} warning threshold must be greater than its critical threshold."
                : $"{Label} warning threshold must be less than its critical threshold.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool TryValidateLoadTriple(out string error)
    {
        var warningValues = ParseTriple(Warning);
        var criticalValues = ParseTriple(Critical);
        if (warningValues == null || criticalValues == null)
        {
            error = $"{Label} thresholds must each contain three comma-separated numbers.";
            return false;
        }

        if (warningValues.Where((value, index) => value >= criticalValues[index]).Any())
        {
            error = $"Every {Label} warning value must be less than its critical value.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static decimal[]? ParseTriple(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
            return null;

        var values = new decimal[3];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!decimal.TryParse(parts[index], NumberStyles.Number, CultureInfo.CurrentCulture, out values[index])
                && !decimal.TryParse(parts[index], NumberStyles.Number, CultureInfo.InvariantCulture, out values[index]))
            {
                return null;
            }
        }

        return values;
    }

    private static bool TryParseThreshold(string text, out decimal value)
    {
        var parsed = decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out value)
                     || decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        return parsed && value is >= 0.01m and <= 100m;
    }
}

public partial class NrpeEditor : ViewModelBase
{
    private static readonly Regex DomainPattern = new(
        @"^(((?!-))(xn--|_)?[a-z0-9-]{0,61}[a-z0-9]\.)*(xn--)?([a-z0-9][a-z0-9\-]{0,60}|[a-z0-9-]{1,30}\.[a-z]{2,})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Host _host;
    private NRPEHostConfiguration? _original;
    private bool _loadStarted;
    private bool _initialEnabled;
    private string _initialAllowedHosts = string.Empty;
    private bool _initialDebug;
    private bool _initialSslLogging;

    public NrpeEditor(Host host)
    {
        _host = host;
    }

    public ObservableCollection<NrpeCheckRow> Checks { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanEditConfiguration))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanEditConfiguration))]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditConfiguration))]
    private bool _enabled;

    [ObservableProperty]
    private string _allowedHosts = string.Empty;

    [ObservableProperty]
    private bool _debugLogging;

    [ObservableProperty]
    private bool _sslLogging;

    [ObservableProperty]
    private string _statusMessage = "Select this page to retrieve the NRPE configuration.";

    public bool CanEdit => IsLoaded && !IsLoading;
    public bool CanEditConfiguration => CanEdit && Enabled;

    public bool HasChanges => IsLoaded
                              && (Enabled != _initialEnabled
                                  || !string.Equals(AllowedHosts, _initialAllowedHosts, StringComparison.Ordinal)
                                  || DebugLogging != _initialDebug
                                  || SslLogging != _initialSslLogging
                                  || Checks.Any(check => check.HasChanges));

    public async System.Threading.Tasks.Task LoadAsync(bool force = false)
    {
        if ((_loadStarted && !force) || IsLoading)
            return;

        _loadStarted = true;
        IsLoading = true;
        StatusMessage = "Retrieving NRPE configuration...";
        var configuration = new NRPEHostConfiguration
        {
            EnableNRPE = false,
            AllowHosts = NRPEHostConfiguration.ALLOW_HOSTS_PLACE_HOLDER,
            Debug = false,
            SslLogging = false,
            Status = NRPEHostConfiguration.RetrieveNRPEStatus.Retrieving
        };

        try
        {
            var action = new NRPERetrieveAction(_host, configuration, suppressHistory: true);
            await System.Threading.Tasks.Task.Run(
                () => action.RunSync(_host.Connection.Session)).ConfigureAwait(true);
            if (configuration.Status != NRPEHostConfiguration.RetrieveNRPEStatus.Successful)
                throw new InvalidOperationException("The host NRPE plugin did not return a valid configuration.");

            _original = (NRPEHostConfiguration)configuration.Clone();
            Populate(configuration);
            IsLoaded = true;
            StatusMessage = "NRPE configuration loaded.";
        }
        catch (Exception ex)
        {
            IsLoaded = false;
            _loadStarted = false;
            StatusMessage = $"Could not retrieve NRPE configuration: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public bool TryValidate(out string error)
    {
        if (!IsLoaded || !Enabled)
        {
            error = string.Empty;
            return true;
        }

        if (!TryValidateAllowedHosts(out error))
            return false;

        foreach (var check in Checks)
        {
            if (!check.TryValidate(out error))
                return false;
        }

        error = string.Empty;
        return true;
    }

    public AsyncAction BuildAction()
    {
        if (_original == null)
            throw new InvalidOperationException("NRPE configuration has not been loaded.");

        var current = new NRPEHostConfiguration
        {
            EnableNRPE = Enabled,
            AllowHosts = string.Join(",", AllowedHosts.Split(',', StringSplitOptions.TrimEntries)),
            Debug = DebugLogging,
            SslLogging = SslLogging,
            Status = NRPEHostConfiguration.RetrieveNRPEStatus.Successful
        };
        foreach (var check in Checks)
        {
            current.AddNRPECheck(new NRPEHostConfiguration.Check(
                check.Name,
                check.Warning.Trim(),
                check.Critical.Trim()));
        }

        return new NRPEUpdateAction(_host, current, _original, suppressHistory: true);
    }

    [RelayCommand]
    private System.Threading.Tasks.Task RetryLoad() => LoadAsync(force: true);

    private void Populate(NRPEHostConfiguration configuration)
    {
        Enabled = configuration.EnableNRPE;
        AllowedHosts = configuration.AllowHosts == NRPEHostConfiguration.ALLOW_HOSTS_PLACE_HOLDER
            ? string.Empty
            : configuration.AllowHosts ?? string.Empty;
        DebugLogging = configuration.Debug;
        SslLogging = configuration.SslLogging;
        Checks.Clear();
        AddCheck(configuration, "check_host_load", "Host load", NrpeThresholdKind.Increasing);
        AddCheck(configuration, "check_host_cpu", "Host CPU usage", NrpeThresholdKind.Increasing);
        AddCheck(configuration, "check_host_memory", "Host memory usage", NrpeThresholdKind.Increasing);
        AddCheck(configuration, "check_vgpu", "vGPU usage", NrpeThresholdKind.Increasing);
        AddCheck(configuration, "check_vgpu_memory", "vGPU memory usage", NrpeThresholdKind.Increasing);
        AddCheck(configuration, "check_load", "Control-domain load", NrpeThresholdKind.LoadTriple);
        AddCheck(configuration, "check_cpu", "Control-domain CPU usage", NrpeThresholdKind.Increasing);
        AddCheck(configuration, "check_memory", "Control-domain memory usage", NrpeThresholdKind.Increasing);
        AddCheck(configuration, "check_swap", "Free swap", NrpeThresholdKind.Decreasing);
        AddCheck(configuration, "check_disk_root", "Free root-disk space", NrpeThresholdKind.Decreasing);
        AddCheck(configuration, "check_disk_log", "Free log-disk space", NrpeThresholdKind.Decreasing);

        _initialEnabled = Enabled;
        _initialAllowedHosts = AllowedHosts;
        _initialDebug = DebugLogging;
        _initialSslLogging = SslLogging;
    }

    private void AddCheck(
        NRPEHostConfiguration configuration,
        string name,
        string label,
        NrpeThresholdKind kind)
    {
        configuration.GetNRPECheck(name, out var check);
        Checks.Add(new NrpeCheckRow(
            name,
            label,
            kind,
            check?.WarningThreshold ?? string.Empty,
            check?.CriticalThreshold ?? string.Empty));
    }

    private bool TryValidateAllowedHosts(out string error)
    {
        var values = AllowedHosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0)
        {
            error = "Enter at least one NRPE client address, CIDR range, or DNS name.";
            return false;
        }

        if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            error = "NRPE allowed-host entries cannot be repeated.";
            return false;
        }

        foreach (var value in values)
        {
            if (!IsValidAllowedHost(value))
            {
                error = $"'{value}' is not a valid IPv4 address, IPv4 CIDR range, or DNS name.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool IsValidAllowedHost(string value)
    {
        var cidrParts = value.Split('/');
        if (cidrParts.Length is 1 or 2
            && IPAddress.TryParse(cidrParts[0], out var address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return cidrParts.Length == 1
                   || int.TryParse(cidrParts[1], out var prefix) && prefix is >= 0 and <= 32;
        }

        return cidrParts.Length == 1 && DomainPattern.IsMatch(value);
    }
}
