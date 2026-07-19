using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Network;
using XenCenterLib;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public string BrandName => "XCP-ng Center";

    public string Tagline => "Manage pools, hosts, and VMs with a calmer console.";

    [ObservableProperty]
    private string _hostInput = string.Empty;

    [ObservableProperty]
    private string _username = "root";

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _showPublicIpWarning;

    [ObservableProperty]
    private bool _acknowledgePublicIp;

    [ObservableProperty]
    private ServerNode? _selectedServer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    private bool _hasServers;

    [ObservableProperty]
    private bool _isBusy;

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
            AcknowledgePublicIp = false;
            return;
        }

        ShowPublicIpWarning = HostnameAddressClassifier.IsPublicIp(host);
        if (!ShowPublicIpWarning)
            AcknowledgePublicIp = false;
    }

    [RelayCommand]
    private void Connect()
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

        if (string.IsNullOrWhiteSpace(Username))
        {
            StatusMessage = "Enter a username.";
            return;
        }

        if (ShowPublicIpWarning && !AcknowledgePublicIp)
        {
            StatusMessage = "Acknowledge the public-IP warning before connecting.";
            return;
        }

        var display = port > 0 ? $"{host}:{port}" : host;
        var node = new ServerNode
        {
            Name = host,
            Address = display,
            Status = "Connecting…",
            IsPublicIp = HostnameAddressClassifier.IsPublicIp(host),
            IsConnecting = true
        };

        Servers.Add(node);
        SelectedServer = node;
        HostInput = string.Empty;
        ShowPublicIpWarning = false;
        AcknowledgePublicIp = false;
        StatusMessage = "Connecting…";
        IsBusy = true;

        BeginLiveConnect(node, host, port > 0 ? port : ConnectionsManager.DEFAULT_XEN_PORT, Username.Trim(), Password);
        Password = string.Empty;
    }

    [RelayCommand]
    private void DisconnectSelected()
    {
        if (SelectedServer?.Connection is null)
            return;

        var node = SelectedServer;
        var conn = node.Connection;
        DetachConnectionHandlers(conn);
        try
        {
            conn.EndConnect();
        }
        catch
        {
            // Best-effort disconnect for preview soak.
        }

        ConnectionsManager.ClearCacheAndRemoveConnection(conn);
        node.Connection = null;
        node.IsConnected = false;
        node.IsConnecting = false;
        node.Status = "Disconnected";
        node.Summary = string.Empty;
        StatusMessage = "Disconnected.";
        IsBusy = false;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedServer is null)
            return;

        if (SelectedServer.Connection != null)
            DisconnectSelected();

        Servers.Remove(SelectedServer);
        SelectedServer = Servers.Count > 0 ? Servers[0] : null;
        StatusMessage = Servers.Count == 0 ? string.Empty : "Server removed.";
    }

    private void BeginLiveConnect(ServerNode node, string host, int port, string username, string password)
    {
        var conn = new XenConnection
        {
            Hostname = host,
            Port = port,
            Username = username,
            Password = password,
            ExpectPasswordIsCorrect = false,
            FriendlyName = host
        };

        node.Connection = conn;

        conn.ConnectionResult += (_, e) => Dispatcher.UIThread.Post(() => OnConnectionResult(node, e));
        conn.CachePopulated += c => Dispatcher.UIThread.Post(() => OnCachePopulated(node, c));
        conn.ConnectionClosed += _ => Dispatcher.UIThread.Post(() => OnConnectionClosed(node));
        conn.ConnectionLost += _ => Dispatcher.UIThread.Post(() =>
        {
            node.IsConnected = false;
            node.IsConnecting = false;
            node.Status = "Connection lost";
            StatusMessage = "Connection lost.";
            IsBusy = false;
        });
        conn.ConnectionMessageChanged += (_, msg) => Dispatcher.UIThread.Post(() =>
        {
            if (node.IsConnecting && !string.IsNullOrWhiteSpace(msg))
                node.Status = msg;
        });

        try
        {
            conn.BeginConnect(initiateCoordinatorSearch: false, PromptForNewPassword);
        }
        catch (Exception ex)
        {
            node.Status = "Connect failed";
            node.IsConnecting = false;
            StatusMessage = ex.Message;
            IsBusy = false;
        }
    }

    private static bool PromptForNewPassword(IXenConnection connection, string oldPassword)
    {
        // Preview: no interactive re-prompt yet — surface as auth failure.
        return false;
    }

    private void OnConnectionResult(ServerNode node, ConnectionResultEventArgs e)
    {
        if (e.Connected)
        {
            node.Status = "Connected — loading inventory…";
            if (!string.IsNullOrEmpty(ShellBootstrap.CertificateValidator.LastMessage))
                StatusMessage = ShellBootstrap.CertificateValidator.LastMessage;
            return;
        }

        node.IsConnecting = false;
        node.IsConnected = false;
        IsBusy = false;

        var reason = !string.IsNullOrWhiteSpace(e.Reason)
            ? e.Reason
            : e.Error?.Message ?? "Connection failed.";
        node.Status = "Failed";
        StatusMessage = reason;
    }

    private void OnCachePopulated(ServerNode node, IXenConnection conn)
    {
        node.IsConnecting = false;
        node.IsConnected = true;
        IsBusy = false;

        var pool = Helpers.GetPoolOfOne(conn);
        var poolName = pool != null ? Helpers.GetName(pool) : conn.Name;
        var hosts = conn.Cache.Hosts?.Length ?? 0;
        var vms = conn.Cache.VMs?.Count(vm => !vm.is_a_template && !vm.is_control_domain) ?? 0;

        node.Name = string.IsNullOrWhiteSpace(poolName) ? conn.Hostname : poolName;
        node.Address = conn.HostnameWithPort;
        node.Status = "Connected";
        node.Summary = $"{hosts} host(s), {vms} VM(s)";
        StatusMessage = $"Connected to {conn.HostnameWithPort}.";
    }

    private void OnConnectionClosed(ServerNode node)
    {
        if (node.IsConnecting)
            return;

        node.IsConnected = false;
        node.IsConnecting = false;
        if (node.Status == "Connected")
            node.Status = "Disconnected";
        IsBusy = false;
    }

    private static void DetachConnectionHandlers(IXenConnection conn)
    {
        // XenConnection events are typical multicast; clearing via new instance is enough for preview.
        // Handlers are closed over node lifetime; EndConnect stops further callbacks for most paths.
        _ = conn;
    }
}
