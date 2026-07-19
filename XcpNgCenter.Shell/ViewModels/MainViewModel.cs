using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenCenterLib;

namespace XcpNgCenter.Shell.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public string BrandName => "XCP-ng Center";

    public string Tagline => "Manage pools, hosts, and VMs with a calmer console.";

    [ObservableProperty]
    private string _hostInput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _showPublicIpWarning;

    [ObservableProperty]
    private ServerNode? _selectedServer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    private bool _hasServers;

    public bool ShowWelcome => !HasServers;

    public ObservableCollection<ServerNode> Servers { get; } = new();

    public MainViewModel()
    {
        Servers.CollectionChanged += (_, _) => HasServers = Servers.Count > 0;
    }

    partial void OnHostInputChanged(string value)
    {
        if (!HostnameAddressClassifier.TryParseHostPort(value.Trim(), out var host, out _))
        {
            ShowPublicIpWarning = false;
            return;
        }

        ShowPublicIpWarning = HostnameAddressClassifier.IsPublicIp(host);
    }

    [RelayCommand]
    private void AddServer()
    {
        var raw = HostInput.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            StatusMessage = "Enter a hostname or IP address.";
            return;
        }

        if (!HostnameAddressClassifier.TryParseHostPort(raw, out var host, out var port))
        {
            StatusMessage = "That does not look like a valid host.";
            return;
        }

        var display = port > 0 ? $"{host}:{port}" : host;
        var node = new ServerNode
        {
            Name = host,
            Address = display,
            Status = "Ready to connect",
            IsPublicIp = HostnameAddressClassifier.IsPublicIp(host)
        };

        Servers.Add(node);
        SelectedServer = node;
        HostInput = string.Empty;
        ShowPublicIpWarning = false;
        StatusMessage = node.IsPublicIp
            ? "Saved. Prefer a VPN or tunnel before connecting to a public management IP."
            : "Server saved. Live xapi connect lands in the next rewrite slice.";
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedServer is null)
            return;

        Servers.Remove(SelectedServer);
        SelectedServer = Servers.Count > 0 ? Servers[0] : null;
        StatusMessage = Servers.Count == 0 ? string.Empty : "Server removed.";
    }
}
