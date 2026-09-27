using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Services;
using Task = System.Threading.Tasks.Task;

namespace XcpNgCenter.Shell.ViewModels;

public sealed record HostIpInterfaceOption(PIF Interface, string Label);

public partial class HostIpEditorViewModel : ViewModelBase
{
    private readonly IXenConnection _connection;
    private readonly Host _host;
    private readonly Action _close;
    private readonly Action<string>? _status;
    private HostIpSnapshot? _baseline;

    public HostIpEditorViewModel(IXenConnection connection, Host host, Action close, Action<string>? status = null)
    {
        _connection = connection;
        _host = host;
        _close = close;
        _status = status;
        Title = $"Configure IP address - {host.Name()}";
        Interfaces = HostIpManagement.Interfaces(host).Select(p => new HostIpInterfaceOption(p,
            $"{p.device}{(p.VLAN >= 0 ? $" / VLAN {p.VLAN}" : "")} - {connection.Resolve(p.network)?.Name()}{(p.management ? " (management)" : "")}")).ToList();
        Families = HostIpManagement.SupportsIpv6(host) ? [HostIpFamily.IPv4, HostIpFamily.IPv6] : [HostIpFamily.IPv4];
        SelectedInterface = Interfaces.FirstOrDefault();
    }

    public string Title { get; }
    public IReadOnlyList<HostIpInterfaceOption> Interfaces { get; }
    public IReadOnlyList<HostIpFamily> Families { get; }
    public IReadOnlyList<HostIpMode> Modes => Family == HostIpFamily.IPv4
        ? [HostIpMode.None, HostIpMode.DHCP, HostIpMode.Static]
        : [HostIpMode.None, HostIpMode.DHCP, HostIpMode.Static, HostIpMode.Autoconf];
    public bool IsIpv4 => Family == HostIpFamily.IPv4;
    public bool IsStatic => Mode == HostIpMode.Static;
    public string AddressLabel => IsIpv4 ? "IPv4 address" : "IPv6 address / prefix length";
    public string DnsLabel => IsIpv4 ? "IPv4 DNS servers (comma separated)" : "IPv6 DNS servers (comma separated)";
    public bool CanSave => !IsSaving && SelectedInterface != null && string.IsNullOrEmpty(InterfaceNotice);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))] private HostIpInterfaceOption? _selectedInterface;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Modes), nameof(IsIpv4), nameof(AddressLabel), nameof(DnsLabel))] private HostIpFamily _family;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsStatic))] private HostIpMode _mode;
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _netmask = "";
    [ObservableProperty] private string _gateway = "";
    [ObservableProperty] private string _dns = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))] private string _interfaceNotice = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))] private bool _isSaving;

    partial void OnSelectedInterfaceChanged(HostIpInterfaceOption? value) => LoadInterface();
    partial void OnFamilyChanged(HostIpFamily value) => LoadInterface();

    private void LoadInterface()
    {
        StatusMessage = "";
        var pif = SelectedInterface?.Interface;
        if (pif == null) { InterfaceNotice = "No host interface is available."; _baseline = null; return; }
        InterfaceNotice = HostIpManagement.InterfaceError(pif) ?? "";
        _baseline = null;
        if (InterfaceNotice.Length == 0)
        {
            try { _baseline = HostIpManagement.Capture(_host, pif); }
            catch (InvalidOperationException ex) { InterfaceNotice = ex.Message; }
        }
        if (Family == HostIpFamily.IPv4)
        {
            Mode = pif.ip_configuration_mode switch { ip_configuration_mode.DHCP => HostIpMode.DHCP, ip_configuration_mode.Static => HostIpMode.Static, _ => HostIpMode.None };
            Address = pif.IP;
            Netmask = pif.netmask;
            Gateway = pif.gateway;
        }
        else
        {
            Mode = pif.ipv6_configuration_mode switch { ipv6_configuration_mode.DHCP => HostIpMode.DHCP, ipv6_configuration_mode.Static => HostIpMode.Static, ipv6_configuration_mode.Autoconf => HostIpMode.Autoconf, _ => HostIpMode.None };
            Address = pif.IPv6.FirstOrDefault() ?? "";
            Gateway = pif.ipv6_gateway;
        }
        Dns = HostIpManagement.DnsForFamily(pif.DNS, Family == HostIpFamily.IPv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6);
    }

    public HostIpEdit Request => new(_baseline ?? throw new InvalidOperationException("Select a host interface."), Family, Mode, Address, Netmask, Gateway, Dns);

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            if (!_connection.IsConnected) throw new InvalidOperationException("The server disconnected. Reconnect and review its current IP configuration.");
            var request = Request;
            var plan = HostIpManagement.Plan(_connection, request);
            if (!plan.Changed) { IsSaving = false; _close(); return; }
            if (!await ShellConfirmPrompt.ConfirmAsync(new ShellConfirmRequest
                {
                    Title = plan.ManagementAddressChanged ? "Change management IP address" : "Change host IP configuration",
                    Message = $"Apply {Family} configuration to {plan.Current.device} on {plan.Host.Name()}? Traffic may be interrupted.\n\n{plan.ReconnectNotice}\n\nConfirm that you have host console access to recover the connection if needed.",
                    AcceptLabel = "Apply IP configuration"
                })) return;
            var action = new HostIpConfigurationAction(_connection, request);
            var succeeded = await ShellActionRunner.RunAndWaitAsync(action, message => { StatusMessage = message; _status?.Invoke(message); });
            if (succeeded)
            {
                _status?.Invoke("Host IP configuration applied. " + plan.ReconnectNotice);
                IsSaving = false;
                _close();
            }
            else
            {
                StatusMessage = $"The change could not be confirmed: {action.Exception?.Message ?? "cancelled"}. The host may already have applied it. {plan.ReconnectNotice} Verify the current configuration before retrying.";
                _status?.Invoke(StatusMessage);
            }
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsSaving = false; }
    }
}
