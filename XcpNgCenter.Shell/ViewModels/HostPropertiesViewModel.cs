using System.Collections.ObjectModel;
using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions;
using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public enum HostPropertiesSection
{
    General,
    CustomFields,
    PerformanceAlerts,
    AlertDelivery,
    PoolPolicies,
    Clustering,
    Nrpe,
    Gpu,
    PowerOn,
    Storage,
    Logging
}

public sealed class HostPropertiesSectionItem
{
    public HostPropertiesSectionItem(HostPropertiesSection section, string title)
    {
        Section = section;
        Title = title;
    }

    public HostPropertiesSection Section { get; }
    public string Title { get; }
}

public sealed class ClusterNetworkOption
{
    public ClusterNetworkOption(XenAPI.Network network, string label)
    {
        Network = network;
        Label = label;
    }

    public XenAPI.Network Network { get; }
    public string Label { get; }

    public override string ToString() => Label;
}

public partial class HostPropertiesViewModel : ViewModelBase
{
    private const string PowerDisabled = "Disabled";
    private const string PowerWakeOnLan = "Wake-on-LAN";
    private const string PowerDrac = "Dell iDRAC";
    private const string PowerIlo = "HPE iLO";
    private const string PowerCustom = "Custom";
    private const string GpuMaximumDensity = "Maximum density";
    private const string GpuMaximumPerformance = "Maximum performance";
    private const string GpuMixedPolicy = "Mixed (leave unchanged)";
    private const string MailEnglish = "English";
    private const string MailChinese = "Chinese";
    private const string MailJapanese = "Japanese";

    private static readonly Regex SyslogDestinationPattern = new(
        @"^[a-zA-Z0-9](?:[-a-zA-Z0-9]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[-a-zA-Z0-9]{0,61}[a-zA-Z0-9])?)*$",
        RegexOptions.CultureInvariant);

    private readonly Host _host;
    private readonly Action _close;
    private readonly Action<string>? _status;
    private readonly bool _initialAutostart;
    private readonly bool _initialMultipathing;
    private readonly string _initialSyslogDestination;
    private readonly Pool? _gpuPool;
    private readonly allocation_algorithm _initialGpuAllocationAlgorithm;
    private readonly bool _initialIntegratedGpuOnNextReboot;
    private readonly Pool? _alertOptionsPool;
    private readonly bool _initialEmailNotificationsEnabled;
    private readonly string _initialAlertEmailAddress;
    private readonly string _initialSmtpServer;
    private readonly string _initialSmtpPort;
    private readonly string _initialMailLanguage;
    private readonly bool _initialLivePatchingEnabled;
    private readonly bool _initialIgmpSnoopingEnabled;
    private readonly bool _initialLegacySslEnabled;
    private readonly bool _initialClusteringEnabled;
    private Host.PowerOnMode? _initialPowerOnMode;
    private bool _powerOnLoadStarted;

    public HostPropertiesViewModel(Host host, Action close, Action<string>? status = null)
    {
        _host = host;
        _close = close;
        _status = status;
        NameLabel = host.name_label ?? string.Empty;
        Description = host.name_description ?? string.Empty;
        AutostartVms = host.GetVmAutostartEnabled();
        _initialAutostart = AutostartVms;
        MultipathingEnabled = host.MultipathEnabled();
        _initialMultipathing = MultipathingEnabled;
        _initialSyslogDestination = host.GetSysLogDestination() ?? string.Empty;
        RemoteLoggingEnabled = !string.IsNullOrWhiteSpace(_initialSyslogDestination);
        SyslogDestination = _initialSyslogDestination;
        CanEditMultipathing = CanChangeMultipathing(host);
        MultipathStatusText = CanEditMultipathing
            ? "Changing multipathing temporarily reconnects attached storage repositories."
            : "Enter maintenance mode before changing multipathing on a live host.";
        Address = string.IsNullOrWhiteSpace(host.address) ? host.hostname ?? string.Empty : host.address;
        Role = Helpers.HostIsCoordinator(host) ? "Coordinator" : "Member";
        Product = FormatProduct(host);
        Uuid = host.uuid ?? string.Empty;
        CustomFields = new CustomFieldsEditor(host);
        PerformanceAlerts = new PerformanceAlertsEditor(host);
        Nrpe = new NrpeEditor(host);
        ShowNrpeSection = Helpers.XapiEqualOrGreater_23_27_0(host.Connection)
                          && (host.Connection.Session.IsLocalSuperuser
                              || host.Connection.Session.Roles.Any(
                                  role => role.name_label == XenAPI.Role.MR_ROLE_POOL_ADMIN));

        _alertOptionsPool = Helpers.GetPoolOfOne(host.Connection);
        ShowAlertDeliverySection = _alertOptionsPool != null;
        MailLanguages.Add(MailEnglish);
        MailLanguages.Add(MailChinese);
        MailLanguages.Add(MailJapanese);
        if (_alertOptionsPool != null)
        {
            _alertOptionsPool.other_config.TryGetValue(Pool.MAIL_DESTINATION_KEY_NAME, out var destination);
            _alertOptionsPool.other_config.TryGetValue(Pool.SMTP_MAILHUB_KEY_NAME, out var mailHub);
            _alertOptionsPool.other_config.TryGetValue(Pool.MAIL_LANGUAGE_KEY_NAME, out var languageCode);
            AlertEmailAddress = destination?.Trim() ?? string.Empty;
            ParseMailHub(mailHub, out var smtpServer, out var smtpPort);
            SmtpServer = smtpServer;
            SmtpPort = smtpPort;
            SelectedMailLanguage = MailLanguageLabel(languageCode);
            EmailNotificationsEnabled = AlertEmailAddress.Length > 0 && SmtpServer.Length > 0;
        }
        _initialEmailNotificationsEnabled = EmailNotificationsEnabled;
        _initialAlertEmailAddress = AlertEmailAddress;
        _initialSmtpServer = SmtpServer;
        _initialSmtpPort = SmtpPort;
        _initialMailLanguage = SelectedMailLanguage;

        _gpuPool = Helpers.GetPoolOfOne(host.Connection);
        var canManagePoolPolicies = _gpuPool != null
                                    && (Helpers.GetPool(host.Connection) == null || Helpers.HostIsCoordinator(host));
        ShowLivePatchingPolicy = canManagePoolPolicies
                                 && !Helpers.FeatureForbidden(host.Connection, Host.RestrictLivePatching)
                                 && !Helpers.CloudOrGreater(host.Connection);
        var coordinator = _gpuPool == null ? null : Helpers.GetCoordinator(_gpuPool);
        ShowIgmpSnoopingPolicy = canManagePoolPolicies
                                 && coordinator != null
                                 && !Helpers.FeatureForbidden(host.Connection, Host.RestrictIGMPSnooping)
                                 && coordinator.vSwitchNetworkBackend();
        ShowLegacySslPolicy = canManagePoolPolicies
                              && !Helpers.FeatureForbidden(host.Connection, Host.RestrictSslLegacySwitch)
                              && !Helpers.StockholmOrGreater(host.Connection);
        ShowPoolPoliciesSection = ShowLivePatchingPolicy || ShowIgmpSnoopingPolicy || ShowLegacySslPolicy;
        LivePatchingEnabled = _gpuPool is { live_patching_disabled: false };
        IgmpSnoopingEnabled = _gpuPool?.igmp_snooping_enabled == true;
        LegacySslEnabled = _gpuPool?.ssl_legacy() == true;
        _initialLivePatchingEnabled = LivePatchingEnabled;
        _initialIgmpSnoopingEnabled = IgmpSnoopingEnabled;
        _initialLegacySslEnabled = LegacySslEnabled;

        var existingCluster = host.Connection.Cache.Clusters.FirstOrDefault();
        // Standalone hosts do not need the Clustering tab unless clustering is already on
        // (so it can still be reviewed/disabled). Multi-host pools keep the tab as usual.
        var isStandalone = Helpers.GetPool(host.Connection) == null;
        ShowClusteringSection = canManagePoolPolicies
                                && !Helpers.FeatureForbidden(host.Connection, Host.RestrictCorosync)
                                && (!isStandalone || existingCluster != null);
        ClusteringEnabled = existingCluster != null;
        _initialClusteringEnabled = ClusteringEnabled;
        PopulateClusterNetworks(existingCluster);
        var canEditClustering = DetermineCanEditClustering(_gpuPool, existingCluster, out var clusteringStatus);
        if (existingCluster == null && ClusterNetworks.Count == 0)
        {
            canEditClustering = false;
            clusteringStatus = "No network with a configured host IP address is available for clustering.";
        }
        CanEditClustering = canEditClustering;
        ClusteringStatusText = clusteringStatus;
        ShowClusterHostCountWarning = _gpuPool != null && host.Connection.Cache.HostCount < 3;

        ShowGpuPlacementPolicy = Helpers.GetPool(host.Connection) == null
                                 && Helpers.VGpuCapability(host.Connection)
                                 && _gpuPool != null;
        ShowIntegratedGpu = host.CanEnableDisableIntegratedGpu();
        ShowGpuSection = ShowGpuPlacementPolicy || ShowIntegratedGpu;
        _initialGpuAllocationAlgorithm = DetermineGpuAllocationAlgorithm(host.Connection.Cache.GPU_groups);
        SelectedGpuPlacementPolicy = GpuPolicyLabel(_initialGpuAllocationAlgorithm);
        if (_initialGpuAllocationAlgorithm == allocation_algorithm.unknown)
            GpuPlacementPolicies.Add(GpuMixedPolicy);
        GpuPlacementPolicies.Add(GpuMaximumDensity);
        GpuPlacementPolicies.Add(GpuMaximumPerformance);

        var currentDisplay = host.display;
        var systemDisplayDevice = host.SystemDisplayDevice();
        var currentDom0Access = systemDisplayDevice?.dom0_access ?? pgpu_dom0_access.unknown;
        var hostCurrentlyEnabled = currentDisplay is host_display.enabled or host_display.disable_on_reboot;
        var hostEnabledOnNextReboot = currentDisplay is host_display.enabled or host_display.enable_on_reboot;
        var gpuCurrentlyEnabled = currentDom0Access is pgpu_dom0_access.enabled or pgpu_dom0_access.disable_on_reboot;
        var gpuEnabledOnNextReboot = currentDom0Access is pgpu_dom0_access.enabled or pgpu_dom0_access.enable_on_reboot;
        IntegratedGpuCurrentlyEnabled = hostCurrentlyEnabled && gpuCurrentlyEnabled;
        IntegratedGpuEnabledOnNextReboot = hostEnabledOnNextReboot && gpuEnabledOnNextReboot;
        _initialIntegratedGpuOnNextReboot = IntegratedGpuEnabledOnNextReboot;

        Sections.Add(new HostPropertiesSectionItem(HostPropertiesSection.General, "General"));
        Sections.Add(new HostPropertiesSectionItem(HostPropertiesSection.CustomFields, "Custom Fields"));
        Sections.Add(new HostPropertiesSectionItem(HostPropertiesSection.PerformanceAlerts, "Performance Alerts"));
        if (ShowAlertDeliverySection)
            Sections.Add(new HostPropertiesSectionItem(HostPropertiesSection.AlertDelivery, "Alert Delivery"));
        if (ShowPoolPoliciesSection)
            Sections.Add(new HostPropertiesSectionItem(HostPropertiesSection.PoolPolicies, "Pool Policies"));
        if (ShowClusteringSection)
            Sections.Add(new HostPropertiesSectionItem(HostPropertiesSection.Clustering, "Clustering"));
        if (ShowNrpeSection)
            Sections.Add(new HostPropertiesSectionItem(HostPropertiesSection.Nrpe, "NRPE"));
        if (ShowGpuSection)
            Sections.Add(new HostPropertiesSectionItem(HostPropertiesSection.Gpu, "GPU"));
        Sections.Add(new HostPropertiesSectionItem(HostPropertiesSection.PowerOn, "Power On"));
        Sections.Add(new HostPropertiesSectionItem(HostPropertiesSection.Storage, "Storage"));
        Sections.Add(new HostPropertiesSectionItem(HostPropertiesSection.Logging, "Logging"));

        PowerOnModeOptions.Add(PowerDisabled);
        PowerOnModeOptions.Add(PowerWakeOnLan);
        PowerOnModeOptions.Add(PowerDrac);
        if (!Helpers.StockholmOrGreater(host.Connection))
            PowerOnModeOptions.Add(PowerIlo);
        PowerOnModeOptions.Add(PowerCustom);
        _selectedPowerOnMode = PowerOnModeLabel(Host.PowerOnMode.Create(host));
        _powerOnStatusText = "Select this page to load the host power-on configuration.";
        SelectedSectionItem = Sections[0];
    }

    public ObservableCollection<HostPropertiesSectionItem> Sections { get; } = new();

    public ObservableCollection<string> PowerOnModeOptions { get; } = new();

    public ObservableCollection<string> GpuPlacementPolicies { get; } = new();

    public ObservableCollection<string> MailLanguages { get; } = new();

    public ObservableCollection<ClusterNetworkOption> ClusterNetworks { get; } = new();

    public string Address { get; }
    public string Role { get; }
    public string Product { get; }
    public string Uuid { get; }
    public CustomFieldsEditor CustomFields { get; }
    public PerformanceAlertsEditor PerformanceAlerts { get; }
    public NrpeEditor Nrpe { get; }
    public bool ShowGpuSection { get; }
    public bool ShowGpuPlacementPolicy { get; }
    public bool ShowIntegratedGpu { get; }
    public bool IntegratedGpuCurrentlyEnabled { get; }
    public bool ShowAlertDeliverySection { get; }
    public bool ShowMailLanguage => _alertOptionsPool != null && Helpers.InvernessOrGreater(_host.Connection);
    public bool ShowPoolPoliciesSection { get; }
    public bool ShowLivePatchingPolicy { get; }
    public bool ShowIgmpSnoopingPolicy { get; }
    public bool ShowLegacySslPolicy { get; }
    public bool ShowClusteringSection { get; }
    public bool CanEditClustering { get; }
    public bool ShowClusterHostCountWarning { get; }
    public string ClusteringStatusText { get; }
    public bool ShowNrpeSection { get; }
    public string IntegratedGpuCurrentStatus => IntegratedGpuCurrentlyEnabled
        ? "The integrated GPU is currently available to the control domain."
        : "The integrated GPU is currently available for passthrough.";
    public bool CanEditMultipathing { get; }
    public string MultipathStatusText { get; }

    [ObservableProperty]
    private HostPropertiesSectionItem? _selectedSectionItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGeneral))]
    [NotifyPropertyChangedFor(nameof(ShowCustomFields))]
    [NotifyPropertyChangedFor(nameof(ShowPerformanceAlerts))]
    [NotifyPropertyChangedFor(nameof(ShowAlertDelivery))]
    [NotifyPropertyChangedFor(nameof(ShowPoolPolicies))]
    [NotifyPropertyChangedFor(nameof(ShowClustering))]
    [NotifyPropertyChangedFor(nameof(ShowNrpe))]
    [NotifyPropertyChangedFor(nameof(ShowGpu))]
    [NotifyPropertyChangedFor(nameof(ShowPowerOn))]
    [NotifyPropertyChangedFor(nameof(ShowStorage))]
    [NotifyPropertyChangedFor(nameof(ShowLogging))]
    private HostPropertiesSection _selectedSection = HostPropertiesSection.General;

    [ObservableProperty]
    private string _nameLabel = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _autostartVms;

    [ObservableProperty]
    private string _selectedGpuPlacementPolicy = GpuMaximumDensity;

    [ObservableProperty]
    private bool _integratedGpuEnabledOnNextReboot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditAlertDelivery))]
    private bool _emailNotificationsEnabled;

    [ObservableProperty]
    private string _alertEmailAddress = string.Empty;

    [ObservableProperty]
    private string _smtpServer = string.Empty;

    [ObservableProperty]
    private string _smtpPort = "25";

    [ObservableProperty]
    private string _selectedMailLanguage = MailEnglish;

    [ObservableProperty]
    private bool _livePatchingEnabled;

    [ObservableProperty]
    private bool _igmpSnoopingEnabled;

    [ObservableProperty]
    private bool _legacySslEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectClusterNetwork))]
    private bool _clusteringEnabled;

    [ObservableProperty]
    private ClusterNetworkOption? _selectedClusterNetwork;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPowerOnCredentials))]
    [NotifyPropertyChangedFor(nameof(ShowCustomPowerOn))]
    private string _selectedPowerOnMode = PowerDisabled;

    [ObservableProperty]
    private string _powerOnAddress = string.Empty;

    [ObservableProperty]
    private string _powerOnUsername = string.Empty;

    [ObservableProperty]
    private string _powerOnPassword = string.Empty;

    [ObservableProperty]
    private string _customPowerOnMode = string.Empty;

    [ObservableProperty]
    private string _customPowerOnConfig = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditPowerOnSettings))]
    private bool _isLoadingPowerOnSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditPowerOnSettings))]
    private bool _isPowerOnSettingsLoaded;

    [ObservableProperty]
    private string _powerOnStatusText = string.Empty;

    [ObservableProperty]
    private bool _multipathingEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSyslogDestination))]
    private bool _remoteLoggingEnabled;

    [ObservableProperty]
    private string _syslogDestination = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool ShowGeneral => SelectedSection == HostPropertiesSection.General;
    public bool ShowCustomFields => SelectedSection == HostPropertiesSection.CustomFields;
    public bool ShowPerformanceAlerts => SelectedSection == HostPropertiesSection.PerformanceAlerts;
    public bool ShowAlertDelivery => SelectedSection == HostPropertiesSection.AlertDelivery;
    public bool ShowPoolPolicies => SelectedSection == HostPropertiesSection.PoolPolicies;
    public bool ShowClustering => SelectedSection == HostPropertiesSection.Clustering;
    public bool ShowNrpe => SelectedSection == HostPropertiesSection.Nrpe;
    public bool ShowGpu => SelectedSection == HostPropertiesSection.Gpu;
    public bool ShowPowerOn => SelectedSection == HostPropertiesSection.PowerOn;
    public bool ShowStorage => SelectedSection == HostPropertiesSection.Storage;
    public bool ShowLogging => SelectedSection == HostPropertiesSection.Logging;
    public bool CanEditSyslogDestination => RemoteLoggingEnabled;
    public bool CanEditAlertDelivery => EmailNotificationsEnabled;
    public bool CanSelectClusterNetwork => CanEditClustering && !ClusteringEnabled;
    public bool CanEditPowerOnSettings => IsPowerOnSettingsLoaded && !IsLoadingPowerOnSettings;
    public bool ShowPowerOnCredentials =>
        string.Equals(SelectedPowerOnMode, PowerDrac, StringComparison.Ordinal)
        || string.Equals(SelectedPowerOnMode, PowerIlo, StringComparison.Ordinal);
    public bool ShowCustomPowerOn =>
        string.Equals(SelectedPowerOnMode, PowerCustom, StringComparison.Ordinal);

    partial void OnSelectedSectionItemChanged(HostPropertiesSectionItem? value)
    {
        if (value != null)
        {
            SelectedSection = value.Section;
            if (value.Section == HostPropertiesSection.PowerOn)
                _ = LoadPowerOnSettingsAsync();
            else if (value.Section == HostPropertiesSection.Nrpe)
                _ = Nrpe.LoadAsync();
        }
    }

    private async System.Threading.Tasks.Task LoadPowerOnSettingsAsync()
    {
        if (_powerOnLoadStarted)
            return;

        _powerOnLoadStarted = true;
        IsLoadingPowerOnSettings = true;
        PowerOnStatusText = "Loading power-on settings...";
        try
        {
            var mode = Host.PowerOnMode.Create(_host);
            await System.Threading.Tasks.Task.Run(() => mode.Load(_host)).ConfigureAwait(true);
            _initialPowerOnMode = mode;
            PopulatePowerOnSettings(mode);
            IsPowerOnSettingsLoaded = true;
            PowerOnStatusText = ShowPowerOnCredentials
                ? "Credentials loaded. Re-enter the controller password when changing these settings."
                : "Power-on settings loaded.";
        }
        catch (Exception ex)
        {
            PowerOnStatusText = $"Could not load power-on settings: {ex.Message}";
        }
        finally
        {
            IsLoadingPowerOnSettings = false;
        }
    }

    private void PopulatePowerOnSettings(Host.PowerOnMode mode)
    {
        SelectedPowerOnMode = PowerOnModeLabel(mode);
        switch (mode)
        {
            case Host.PowerOnModeDRAC drac:
                PowerOnAddress = drac.IpAddress;
                PowerOnUsername = drac.Username;
                PowerOnPassword = drac.Password;
                break;
            case Host.PowerOnModeiLO ilo:
                PowerOnAddress = ilo.IpAddress;
                PowerOnUsername = ilo.Username;
                PowerOnPassword = ilo.Password;
                break;
            case Host.PowerOnModeCustom custom:
                CustomPowerOnMode = custom.CustomMode;
                CustomPowerOnConfig = string.Join(
                    Environment.NewLine,
                    custom.CustomConfig
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{pair.Key}={pair.Value}"));
                break;
        }
    }

    [RelayCommand]
    private void Apply()
    {
        if (string.IsNullOrWhiteSpace(NameLabel))
        {
            ShowValidationError("Enter a host name.", HostPropertiesSection.General);
            return;
        }

        var syslogDestination = RemoteLoggingEnabled ? SyslogDestination.Trim() : string.Empty;
        if (RemoteLoggingEnabled && !IsValidSyslogDestination(syslogDestination))
        {
            ShowValidationError(
                "Enter a valid remote syslog hostname or IPv4 address.",
                HostPropertiesSection.Logging);
            return;
        }

        if (!PerformanceAlerts.TryValidate(out var performanceAlertError))
        {
            ShowValidationError(performanceAlertError, HostPropertiesSection.PerformanceAlerts);
            return;
        }

        if (!CustomFields.TryValidate(out var customFieldError))
        {
            ShowValidationError(customFieldError, HostPropertiesSection.CustomFields);
            return;
        }

        if (EmailNotificationsEnabled && !TryValidateAlertDelivery(out var alertDeliveryError))
        {
            ShowValidationError(alertDeliveryError, HostPropertiesSection.AlertDelivery);
            return;
        }

        if (ShowClusteringSection
            && ClusteringEnabled != _initialClusteringEnabled
            && ClusteringEnabled
            && SelectedClusterNetwork == null)
        {
            ShowValidationError(
                "Select a network with a configured host IP address for clustering.",
                HostPropertiesSection.Clustering);
            return;
        }

        if (ShowNrpeSection && !Nrpe.TryValidate(out var nrpeError))
        {
            ShowValidationError(nrpeError, HostPropertiesSection.Nrpe);
            return;
        }

        Host.PowerOnMode? powerOnMode = null;
        var powerOnChanged = false;
        if (IsPowerOnSettingsLoaded)
        {
            if (!TryBuildPowerOnMode(out powerOnMode, out var powerOnError))
            {
                ShowValidationError(powerOnError, HostPropertiesSection.PowerOn);
                return;
            }

            powerOnChanged = _initialPowerOnMode != null
                             && PowerOnSettingsChanged(_initialPowerOnMode, powerOnMode);
            if (powerOnChanged
                && powerOnMode is Host.PowerOnModeDRAC or Host.PowerOnModeiLO
                && string.IsNullOrEmpty(PowerOnPassword))
            {
                ShowValidationError(
                    "Re-enter the management-controller password before saving changed credentials.",
                    HostPropertiesSection.PowerOn);
                return;
            }
        }

        var name = NameLabel.Trim();
        var description = Description ?? string.Empty;
        var actions = new List<AsyncAction>();

        if (!string.Equals(name, _host.name_label, StringComparison.Ordinal))
        {
            actions.Add(new DelegatedAsyncAction(
                _host.Connection,
                $"Rename {_host.name_label}",
                "Renaming...",
                "Renamed.",
                session => Host.set_name_label(session, _host.opaque_ref, name),
                true,
                "host.set_name_label"));
        }

        if (!string.Equals(description, _host.name_description ?? string.Empty, StringComparison.Ordinal))
        {
            actions.Add(new DelegatedAsyncAction(
                _host.Connection,
                $"Update description for {name}",
                "Updating description...",
                "Description updated.",
                session => Host.set_name_description(session, _host.opaque_ref, description),
                true,
                "host.set_name_description"));
        }

        if (AutostartVms != _initialAutostart)
            actions.Add(new ChangeHostAutostartAction(_host, AutostartVms));

        if (PerformanceAlerts.HasChanges)
            actions.Add(new PerfmonDefinitionAction(_host, PerformanceAlerts.BuildDefinitions(), suppressHistory: true));

        if (CustomFields.HasChanges)
            CustomFields.AppendActions(actions);

        var selectedGpuAlgorithm = GpuPolicyValue(SelectedGpuPlacementPolicy);
        if (ShowGpuPlacementPolicy
            && _gpuPool != null
            && selectedGpuAlgorithm != allocation_algorithm.unknown
            && selectedGpuAlgorithm != _initialGpuAllocationAlgorithm)
        {
            actions.Add(new SetGpuPlacementPolicyAction(_gpuPool, selectedGpuAlgorithm));
        }

        if (ShowIntegratedGpu
            && IntegratedGpuEnabledOnNextReboot != _initialIntegratedGpuOnNextReboot)
        {
            actions.Add(new UpdateIntegratedGpuPassthroughAction(
                _host,
                IntegratedGpuEnabledOnNextReboot,
                suppressHistory: true));
        }

        if (AlertDeliveryChanged())
        {
            actions.Add(new PerfmonOptionsDefinitionAction(
                _host.Connection,
                EmailNotificationsEnabled ? AlertEmailAddress.Trim() : null,
                EmailNotificationsEnabled ? $"{SmtpServer.Trim()}:{SmtpPort.Trim()}" : null,
                EmailNotificationsEnabled && ShowMailLanguage ? MailLanguageCode(SelectedMailLanguage) : null,
                suppressHistory: true));
        }

        if (_gpuPool != null && ShowLivePatchingPolicy && LivePatchingEnabled != _initialLivePatchingEnabled)
        {
            actions.Add(new DelegatedAsyncAction(
                _host.Connection,
                LivePatchingEnabled ? "Enable live patching" : "Disable live patching",
                LivePatchingEnabled ? "Enabling live patching..." : "Disabling live patching...",
                "Live-patching policy updated.",
                session => Pool.set_live_patching_disabled(
                    session,
                    _gpuPool.opaque_ref,
                    !LivePatchingEnabled),
                true,
                "pool.set_live_patching_disabled"));
        }

        if (_gpuPool != null && ShowIgmpSnoopingPolicy && IgmpSnoopingEnabled != _initialIgmpSnoopingEnabled)
        {
            actions.Add(new DelegatedAsyncAction(
                _host.Connection,
                IgmpSnoopingEnabled ? "Enable IGMP snooping" : "Disable IGMP snooping",
                IgmpSnoopingEnabled ? "Enabling IGMP snooping..." : "Disabling IGMP snooping...",
                "IGMP-snooping policy updated.",
                session => Pool.set_igmp_snooping_enabled(
                    session,
                    _gpuPool.opaque_ref,
                    IgmpSnoopingEnabled),
                true,
                "pool.set_igmp_snooping_enabled"));
        }

        if (_gpuPool != null && ShowLegacySslPolicy && LegacySslEnabled != _initialLegacySslEnabled)
            actions.Add(new SetSslLegacyAction(_gpuPool, LegacySslEnabled));

        if (_gpuPool != null
            && ShowClusteringSection
            && CanEditClustering
            && ClusteringEnabled != _initialClusteringEnabled)
        {
            if (ClusteringEnabled && SelectedClusterNetwork != null)
                actions.Add(new EnableClusteringAction(_gpuPool, SelectedClusterNetwork.Network));
            else if (!ClusteringEnabled)
                actions.Add(new DisableClusteringAction(_gpuPool));
        }

        if (ShowNrpeSection && Nrpe.HasChanges)
            actions.Add(Nrpe.BuildAction());

        if (powerOnChanged && powerOnMode != null)
        {
            actions.Add(new SavePowerOnSettingsAction(
                _host.Connection,
                [new KeyValuePair<Host, Host.PowerOnMode>(_host, powerOnMode)],
                suppressHistory: true));
        }

        if (CanEditMultipathing && MultipathingEnabled != _initialMultipathing)
            actions.Add(new EditMultipathAction(_host, MultipathingEnabled, suppressHistory: true));

        if (!string.Equals(syslogDestination, _initialSyslogDestination, StringComparison.Ordinal))
            actions.Add(CreateSyslogAction(syslogDestination));

        if (actions.Count == 0)
        {
            _close();
            return;
        }

        var multi = new MultipleAction(
            _host.Connection,
            $"Update properties for {name}",
            "Updating host properties...",
            $"Updated properties for {name}.",
            actions,
            stopOnFirstException: true);

        ShellActionRunner.Run(multi, msg =>
        {
            StatusMessage = msg;
            _status?.Invoke(msg);
        });

        _status?.Invoke("Host property changes queued - see Logs.");
        _close();
    }

    private bool TryBuildPowerOnMode(out Host.PowerOnMode mode, out string error)
    {
        error = string.Empty;
        switch (SelectedPowerOnMode)
        {
            case PowerDisabled:
                mode = new Host.PowerOnModeDisabled();
                return true;
            case PowerWakeOnLan:
                mode = new Host.PowerOnModeWakeOnLan();
                return true;
            case PowerDrac:
                if (!IPAddress.TryParse(PowerOnAddress.Trim(), out _))
                {
                    mode = new Host.PowerOnModeDisabled();
                    error = "Enter a valid iDRAC IPv4 or IPv6 address.";
                    return false;
                }

                mode = new Host.PowerOnModeDRAC
                {
                    IpAddress = PowerOnAddress.Trim(),
                    Username = PowerOnUsername.Trim(),
                    Password = PowerOnPassword
                };
                return true;
            case PowerIlo:
                if (!IPAddress.TryParse(PowerOnAddress.Trim(), out _))
                {
                    mode = new Host.PowerOnModeDisabled();
                    error = "Enter a valid iLO IPv4 or IPv6 address.";
                    return false;
                }

                mode = new Host.PowerOnModeiLO
                {
                    IpAddress = PowerOnAddress.Trim(),
                    Username = PowerOnUsername.Trim(),
                    Password = PowerOnPassword
                };
                return true;
            case PowerCustom:
                var customMode = CustomPowerOnMode.Trim();
                if (customMode.Length == 0)
                {
                    mode = new Host.PowerOnModeDisabled();
                    error = "Enter the custom power-on mode name.";
                    return false;
                }

                var custom = new Host.PowerOnModeCustom { CustomMode = customMode };
                foreach (var rawLine in CustomPowerOnConfig.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = rawLine.Trim();
                    var separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        mode = new Host.PowerOnModeDisabled();
                        error = $"Use key=value for custom setting '{line}'.";
                        return false;
                    }

                    var key = line[..separator].Trim();
                    var value = line[(separator + 1)..].Trim();
                    if (key.Length == 0 || value.Length == 0)
                    {
                        mode = new Host.PowerOnModeDisabled();
                        error = "Custom power-on setting keys and values cannot be empty.";
                        return false;
                    }

                    if (!custom.CustomConfig.TryAdd(key, value))
                    {
                        mode = new Host.PowerOnModeDisabled();
                        error = $"Custom power-on setting '{key}' is repeated.";
                        return false;
                    }
                }

                mode = custom;
                return true;
            default:
                mode = new Host.PowerOnModeDisabled();
                error = "Choose a power-on mode.";
                return false;
        }
    }

    private static bool PowerOnSettingsChanged(Host.PowerOnMode initial, Host.PowerOnMode current)
    {
        if (!string.Equals(initial.ToString(), current.ToString(), StringComparison.Ordinal))
            return true;

        var initialConfig = initial.Config;
        var currentConfig = current.Config;
        return initialConfig.Count != currentConfig.Count
               || initialConfig.Any(pair =>
                   !currentConfig.TryGetValue(pair.Key, out var value)
                   || !string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static string PowerOnModeLabel(Host.PowerOnMode mode) => mode switch
    {
        Host.PowerOnModeWakeOnLan => PowerWakeOnLan,
        Host.PowerOnModeDRAC => PowerDrac,
        Host.PowerOnModeiLO => PowerIlo,
        Host.PowerOnModeCustom => PowerCustom,
        _ => PowerDisabled
    };

    private DelegatedAsyncAction CreateSyslogAction(string destination)
    {
        var logging = _host.logging == null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(_host.logging);

        if (string.IsNullOrEmpty(destination))
            logging.Remove("syslog_destination");
        else
            logging["syslog_destination"] = destination;

        return new DelegatedAsyncAction(
            _host.Connection,
            "Change log destination",
            "Changing remote log destination...",
            "Remote log destination updated.",
            session =>
            {
                Host.set_logging(session, _host.opaque_ref, logging);
                Host.syslog_reconfigure(session, _host.opaque_ref);
            },
            true,
            "host.set_logging",
            "host.syslog_reconfigure");
    }

    private void ShowValidationError(string message, HostPropertiesSection section)
    {
        StatusMessage = message;
        SelectedSection = section;
        SelectedSectionItem = Sections.First(item => item.Section == section);
    }

    [RelayCommand]
    private void Cancel() => _close();

    private static bool CanChangeMultipathing(Host host)
    {
        var metrics = host.Connection.Resolve(host.metrics);
        return host.MaintenanceMode() || metrics is { live: false };
    }

    private static bool IsValidSyslogDestination(string destination) =>
        destination.Length <= 253 && SyslogDestinationPattern.IsMatch(destination);

    private static string FormatProduct(Host host)
    {
        var product = host.ProductVersionText();
        var brand = host.ProductBrand();
        if (string.IsNullOrWhiteSpace(product))
            return brand ?? "-";
        if (string.IsNullOrWhiteSpace(brand))
            return product;
        return $"{brand} {product}";
    }

    private static allocation_algorithm DetermineGpuAllocationAlgorithm(IEnumerable<GPU_group> groups)
    {
        var algorithms = groups
            .Select(group => group.allocation_algorithm)
            .Distinct()
            .ToList();
        return algorithms.Count == 1 ? algorithms[0] : allocation_algorithm.unknown;
    }

    private static string GpuPolicyLabel(allocation_algorithm algorithm) => algorithm switch
    {
        allocation_algorithm.depth_first => GpuMaximumDensity,
        allocation_algorithm.breadth_first => GpuMaximumPerformance,
        _ => GpuMixedPolicy
    };

    private static allocation_algorithm GpuPolicyValue(string label) => label switch
    {
        GpuMaximumDensity => allocation_algorithm.depth_first,
        GpuMaximumPerformance => allocation_algorithm.breadth_first,
        _ => allocation_algorithm.unknown
    };

    private bool TryValidateAlertDelivery(out string error)
    {
        try
        {
            var address = new MailAddress(AlertEmailAddress.Trim());
            if (!string.Equals(address.Address, AlertEmailAddress.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new FormatException();
        }
        catch
        {
            error = "Enter one valid destination email address.";
            return false;
        }

        var smtp = SmtpServer.Trim();
        if (smtp.Length == 0 || smtp.Any(character => character >= 128)
            || Uri.CheckHostName(smtp) is UriHostNameType.Unknown or UriHostNameType.IPv6)
        {
            error = "Enter a valid SMTP hostname or IPv4 address.";
            return false;
        }

        if (!int.TryParse(SmtpPort.Trim(), out var port) || port is < 1 or > 65535)
        {
            error = "Enter an SMTP port between 1 and 65,535.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool AlertDeliveryChanged() =>
        EmailNotificationsEnabled != _initialEmailNotificationsEnabled
        || !string.Equals(AlertEmailAddress, _initialAlertEmailAddress, StringComparison.Ordinal)
        || !string.Equals(SmtpServer, _initialSmtpServer, StringComparison.Ordinal)
        || !string.Equals(SmtpPort, _initialSmtpPort, StringComparison.Ordinal)
        || !string.Equals(SelectedMailLanguage, _initialMailLanguage, StringComparison.Ordinal);

    private static void ParseMailHub(string? mailHub, out string server, out string port)
    {
        server = string.Empty;
        port = "25";
        if (string.IsNullOrWhiteSpace(mailHub))
            return;

        var parts = mailHub.Trim().Split(':');
        if (parts.Length > 0)
            server = parts[0];
        if (parts.Length > 1 && parts[1].Length > 0)
            port = parts[1];
    }

    private static string MailLanguageLabel(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "zh" or "zh-cn" => MailChinese,
        "ja" or "ja-jp" => MailJapanese,
        _ => MailEnglish
    };

    private static string MailLanguageCode(string label) => label switch
    {
        MailChinese => "zh-CN",
        MailJapanese => "ja-JP",
        _ => "en-US"
    };

    private void PopulateClusterNetworks(Cluster? existingCluster)
    {
        var selectedNetworkRef = existingCluster?.cluster_hosts
            .Select(reference => _host.Connection.Resolve(reference))
            .Where(clusterHost => clusterHost != null)
            .Select(clusterHost => _host.Connection.Resolve(clusterHost!.PIF))
            .Where(pif => pif != null)
            .Select(pif => pif!.network?.opaque_ref)
            .FirstOrDefault(reference => !string.IsNullOrEmpty(reference));

        foreach (var pif in _host.Connection.Cache.PIFs
                     .Where(pif => pif.IsManagementInterface(false))
                     .OrderByDescending(pif => pif.management)
                     .ThenBy(pif => pif.device, StringComparer.OrdinalIgnoreCase))
        {
            var network = _host.Connection.Resolve(pif.network);
            if (network == null || ClusterNetworks.Any(option => option.Network.opaque_ref == network.opaque_ref))
                continue;

            var label = pif.management ? $"{network.Name()} (management)" : network.Name();
            var option = new ClusterNetworkOption(network, label);
            ClusterNetworks.Add(option);
            if (string.Equals(network.opaque_ref, selectedNetworkRef, StringComparison.Ordinal))
                SelectedClusterNetwork = option;
        }

        SelectedClusterNetwork ??= ClusterNetworks.FirstOrDefault();
    }

    private static bool DetermineCanEditClustering(Pool? pool, Cluster? existingCluster, out string status)
    {
        if (pool == null)
        {
            status = "Pool information is unavailable.";
            return false;
        }

        if (existingCluster != null
            && pool.Connection.Cache.SRs.Any(sr => sr.GetSRType(true) == SR.SRTypes.gfs2 && !sr.IsDetached()))
        {
            status = "Clustering cannot be disabled while a GFS2 storage repository is attached.";
            return false;
        }

        if (existingCluster == null && pool.ha_enabled)
        {
            status = "Disable high availability before enabling clustering.";
            return false;
        }

        if (existingCluster == null
            && !pool.Connection.Cache.Hosts.Any(Host.RestrictPoolSecretRotation)
            && pool.is_psr_pending)
        {
            status = "Finish the pending pool-secret rotation before enabling clustering.";
            return false;
        }

        status = existingCluster == null
            ? "Clustering is disabled. Select an IP-configured network before enabling it."
            : "Clustering is enabled. Its network cannot be changed without disabling clustering first.";
        return true;
    }
}
