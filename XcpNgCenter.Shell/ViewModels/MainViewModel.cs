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
    private InfraTreeNode? _selectedInfraNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    [NotifyPropertyChangedFor(nameof(ShowInfrastructure))]
    private bool _hasServers;

    [ObservableProperty]
    private bool _isBusy;

    public bool ShowWelcome => !HasServers;

    public bool ShowInfrastructure => HasServers;

    public ObservableCollection<ServerNode> Servers { get; } = new();

    public ObservableCollection<InfraTreeNode> InfrastructureRoots { get; } = new();

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

    partial void OnSelectedInfraNodeChanged(InfraTreeNode? value)
    {
        if (value?.Server != null)
            SelectedServer = value.Server;
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
        var server = SelectedInfraNode?.Server ?? SelectedServer;
        if (server?.Connection is null)
            return;

        var conn = server.Connection;
        try
        {
            conn.EndConnect();
        }
        catch
        {
            // Best-effort disconnect for preview soak.
        }

        ConnectionsManager.ClearCacheAndRemoveConnection(conn);
        server.Connection = null;
        server.IsConnected = false;
        server.IsConnecting = false;
        server.Status = "Disconnected";
        server.Summary = string.Empty;
        RemoveTreeForServer(server);
        SelectedInfraNode = null;
        StatusMessage = "Disconnected.";
        IsBusy = false;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        var server = SelectedInfraNode?.Server ?? SelectedServer;
        if (server is null)
            return;

        if (server.Connection != null)
        {
            SelectedServer = server;
            DisconnectSelected();
        }

        RemoveTreeForServer(server);
        Servers.Remove(server);
        SelectedServer = Servers.Count > 0 ? Servers[0] : null;
        SelectedInfraNode = InfrastructureRoots.FirstOrDefault();
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
        conn.XenObjectsUpdated += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (node.Connection != null)
                RebuildTreeForServer(node, node.Connection);
        });
        conn.ConnectionClosed += _ => Dispatcher.UIThread.Post(() => OnConnectionClosed(node));
        conn.ConnectionLost += _ => Dispatcher.UIThread.Post(() =>
        {
            node.IsConnected = false;
            node.IsConnecting = false;
            node.Status = "Connection lost";
            RemoveTreeForServer(node);
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
        => false;

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
        var vms = conn.Cache.VMs?.Count(vm => vm.IsRealVm()) ?? 0;

        node.Name = string.IsNullOrWhiteSpace(poolName) ? conn.Hostname : poolName;
        node.Address = conn.HostnameWithPort;
        node.Status = "Connected";
        node.Summary = $"{hosts} host(s), {vms} VM(s)";
        StatusMessage = $"Connected to {conn.HostnameWithPort}.";

        RebuildTreeForServer(node, conn, selectRoot: true);
    }

    private void OnConnectionClosed(ServerNode node)
    {
        if (node.IsConnecting)
            return;

        node.IsConnected = false;
        node.IsConnecting = false;
        if (node.Status == "Connected")
            node.Status = "Disconnected";
        RemoveTreeForServer(node);
        IsBusy = false;
    }

    private void RebuildTreeForServer(ServerNode server, IXenConnection conn, bool selectRoot = false)
    {
        if (!server.IsConnected || !ReferenceEquals(server.Connection, conn))
            return;

        var selectedRef = SelectedInfraNode?.OpaqueRef;
        var root = InfrastructureTreeBuilder.Build(server, conn);

        var existing = InfrastructureRoots.FirstOrDefault(r => r.Server == server);
        if (existing != null)
        {
            var index = InfrastructureRoots.IndexOf(existing);
            InfrastructureRoots[index] = root;
        }
        else
        {
            InfrastructureRoots.Add(root);
        }

        if (selectRoot || SelectedInfraNode?.Server == server)
        {
            SelectedInfraNode = FindByOpaqueRef(root, selectedRef) ?? root;
            SelectedServer = server;
        }
    }

    private void RemoveTreeForServer(ServerNode server)
    {
        for (var i = InfrastructureRoots.Count - 1; i >= 0; i--)
        {
            if (InfrastructureRoots[i].Server == server)
                InfrastructureRoots.RemoveAt(i);
        }

        if (SelectedInfraNode?.Server == server)
            SelectedInfraNode = InfrastructureRoots.FirstOrDefault();
    }

    private static InfraTreeNode? FindByOpaqueRef(InfraTreeNode node, string? opaqueRef)
    {
        if (string.IsNullOrEmpty(opaqueRef))
            return null;
        if (node.OpaqueRef == opaqueRef)
            return node;
        foreach (var child in node.Children)
        {
            var match = FindByOpaqueRef(child, opaqueRef);
            if (match != null)
                return match;
        }
        return null;
    }
}
