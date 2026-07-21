using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin;
using XenAdmin.Core;
using XenAdmin.Network;
using XenCenterLib;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly HostedConsoleSession _consoleSession = new();
    private readonly SavedServerStore _savedServerStore = new();
    private bool _disposed;

    public string BrandName => "XCP-ng Center";

    public string Tagline => "Manage pools, hosts, and VMs with a calmer console.";

    public HostedConsoleSession ConsoleSession => _consoleSession;

    /// <summary>Windows DPAPI can store passwords; other platforms leave the checkbox disabled.</summary>
    public bool CanPersistPasswords => SavedServerStore.CanPersistPasswords;

    public string RememberPasswordLabel => CanPersistPasswords
        ? "Remember password for this server (Windows DPAPI)"
        : "Remember password (unavailable on this platform)";

    public string PasswordVaultHint => CanPersistPasswords
        ? "Passwords are encrypted with Windows DPAPI for the current user when remembered."
        : "Password vault requires Windows DPAPI — hosts and usernames still save on this platform.";

    [ObservableProperty]
    private string _hostInput = string.Empty;

    [ObservableProperty]
    private string _username = "root";

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _rememberPassword;

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
    [NotifyPropertyChangedFor(nameof(ShowInfrastructureDetail))]
    private bool _hasServers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    [NotifyPropertyChangedFor(nameof(ShowInfrastructureDetail))]
    private bool _showGlobalAlerts;

    [ObservableProperty]
    private bool _isBusy;

    public bool ShowWelcome => !HasServers && !ShowGlobalAlerts;

    /// <summary>Sidebar tree and actions when any server is connected.</summary>
    public bool ShowInfrastructure => HasServers;

    /// <summary>Object detail pane (hidden while the global alerts pane is open).</summary>
    public bool ShowInfrastructureDetail => HasServers && !ShowGlobalAlerts;

    public ObservableCollection<ServerNode> Servers { get; } = new();

    public ObservableCollection<SavedServerEntry> SavedServers { get; } = new();

    public bool HasSavedServers => SavedServers.Count > 0;

    public bool HasTrustedCertificates => ShellBootstrap.CertificateStore.Count > 0;

    public string ClearPinsLabel => HasTrustedCertificates
        ? $"Clear trusted certificates ({ShellBootstrap.CertificateStore.Count})"
        : "Clear trusted certificates";

    public ObservableCollection<InfraTreeNode> InfrastructureRoots { get; } = new();

    public ObservableCollection<GeneralPropertyRow> GeneralProperties { get; } = new();

    public ObservableCollection<GeneralPropertyRow> StorageTotals { get; } = new();

    public ObservableCollection<StorageItemRow> StorageItems { get; } = new();

    public ObservableCollection<GeneralPropertyRow> NetworkTotals { get; } = new();

    public ObservableCollection<NetworkItemRow> NetworkItems { get; } = new();

    public ObservableCollection<NetworkItemRow> NetworkMgmtItems { get; } = new();

    public ObservableCollection<GeneralPropertyRow> ConsoleTotals { get; } = new();

    public ObservableCollection<ConsoleItemRow> ConsoleItems { get; } = new();

    public ObservableCollection<SnapshotItemRow> SnapshotItems { get; } = new();

    [ObservableProperty]
    private bool _hasStorageItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyNetworkContent))]
    private bool _hasNetworkItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyNetworkContent))]
    private bool _hasNetworkMgmtItems;

    public bool HasAnyNetworkContent => HasNetworkItems || HasNetworkMgmtItems;

    [ObservableProperty]
    private bool _hasConsoleItems;

    [ObservableProperty]
    private bool _hasSnapshotItems;

    [ObservableProperty]
    private bool _canManageSnapshots;

    [ObservableProperty]
    private string _newSnapshotName = string.Empty;

    [ObservableProperty]
    private string _newSnapshotDescription = string.Empty;

    [ObservableProperty]
    private string _snapshotStatusMessage = string.Empty;

    [ObservableProperty]
    private string _consoleStatusMessage = string.Empty;

    [ObservableProperty]
    private string _consolePlaceholderMessage = string.Empty;

    [ObservableProperty]
    private string _consoleCopyFeedback = string.Empty;

    [ObservableProperty]
    private string _propertyCopyFeedback = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConsoleFrame))]
    private WriteableBitmap? _consoleBitmap;

    [ObservableProperty]
    private string _consoleViewerStatus = string.Empty;

    [ObservableProperty]
    private string _consoleInputHint = string.Empty;

    [ObservableProperty]
    private bool _isConsoleConnecting;

    public bool HasConsoleFrame => ConsoleBitmap != null;

    /// <summary>
    /// Last intentional tree selection used for detail panes. Avalonia TreeView often
    /// clears SelectedItem when focus moves (tabs) or when the tree is rebuilt on cache updates.
    /// </summary>
    private InfraTreeNode? _pinnedInfraNode;

    private bool _suppressSelectionClear;
    private bool _restoreSelectionQueued;
    private string? _activeConsoleKey;

    /// <summary>Raised around infrastructure tree rebuilds/selection restores so the view can keep scroll position.</summary>
    public event Action? TreeLayoutChanging;

    public event Action? TreeLayoutChanged;

    public MainViewModel()
    {
        RememberPassword = CanPersistPasswords;
        Servers.CollectionChanged += (_, _) => HasServers = Servers.Count > 0;
        SavedServers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSavedServers));
        _consoleSession.StateChanged += OnConsoleSessionStateChanged;
        LoadSavedServers();
        RefreshTrustUi();
        InitializeActionHistoryUi();
        InitializeAlertsAndGraphsUi();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DisposeAlertsAndGraphsUi();
        DisposeActionHistoryUi();
        _consoleSession.StateChanged -= OnConsoleSessionStateChanged;
        try
        {
            _consoleSession.Dispose();
        }
        catch
        {
            // Best-effort shutdown.
        }

        foreach (var server in Servers.ToList())
        {
            if (server.Connection is null)
                continue;
            try
            {
                DetachAlertsForConnection(server.Connection);
                server.Connection.EndConnect();
            }
            catch
            {
                // Best-effort disconnect on exit.
            }

            try
            {
                ConnectionsManager.ClearCacheAndRemoveConnection(server.Connection);
            }
            catch
            {
                // Ignore cleanup failures during shutdown.
            }

            server.Connection = null;
        }
    }

    public void SetConsoleInputFocused(bool focused)
    {
        if (!HasConsoleFrame)
        {
            ConsoleInputHint = string.Empty;
            return;
        }

        ConsoleInputHint = focused
            ? "Keyboard and mouse captured by guest — click elsewhere to release."
            : "Click the console to send keyboard and mouse to the guest.";
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
        if (value != null && ShowGlobalAlerts)
            ShowGlobalAlerts = false;

        if (value == null)
        {
            if (_suppressSelectionClear || _pinnedInfraNode == null || !HasServers)
            {
                if (_pinnedInfraNode == null)
                    RefreshDetailPanes();
                return;
            }

            QueueRestorePinnedSelection();
            return;
        }

        _pinnedInfraNode = value;
        if (value.Server != null)
            SelectedServer = value.Server;
        RefreshDetailPanes();
    }

    [RelayCommand]
    private void Connect()
    {
        var error = TryBeginConnect(HostInput, Username, Password, RememberPassword);
        if (error != null)
            StatusMessage = error;
    }

    /// <summary>
    /// Starts a live connect. Returns an error message, or null when the connect was queued.
    /// </summary>
    public string? TryBeginConnect(string hostInput, string usernameInput, string password, bool rememberPassword)
    {
        var raw = hostInput.Trim();
        if (string.IsNullOrEmpty(raw))
            return "Enter a hostname or IP address.";

        if (!HostnameAddressClassifier.TryParseHostPort(raw, out var host, out var port))
            return "That does not look like a valid host.";

        if (string.IsNullOrWhiteSpace(usernameInput))
            return "Enter a username.";

        var isPublic = HostnameAddressClassifier.IsPublicIp(host);
        if (isPublic && ShowWelcome && ShowPublicIpWarning && !AcknowledgePublicIp)
            return "Acknowledge the public-IP warning before connecting.";

        // Avoid duplicate live connections to the same address.
        var display = port > 0 ? $"{host}:{port}" : host;
        if (Servers.Any(s => s.IsConnected
                             && string.Equals(s.Address, display, StringComparison.OrdinalIgnoreCase)))
            return $"Already connected to {display}.";

        var username = usernameInput.Trim();
        var node = new ServerNode
        {
            Name = host,
            Address = display,
            Status = "Connecting…",
            IsPublicIp = isPublic,
            IsConnecting = true,
            Username = username
        };

        Servers.Add(node);
        SelectedServer = node;
        HostInput = string.Empty;
        ShowPublicIpWarning = false;
        AcknowledgePublicIp = false;
        StatusMessage = $"Connecting to {display}…";
        IsBusy = true;

        RememberServer(display, username, rememberPassword && CanPersistPasswords ? password : null);
        BeginLiveConnect(node, host, port > 0 ? port : ConnectionsManager.DEFAULT_XEN_PORT, username, password);
        Password = string.Empty;
        return null;
    }

    [RelayCommand]
    private void UseSavedServer(SavedServerEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Address))
            return;

        HostInput = entry.Address;
        if (!string.IsNullOrWhiteSpace(entry.Username))
            Username = entry.Username;

        var restored = SavedServerStore.UnprotectPassword(entry.EncryptedPassword);
        if (!string.IsNullOrEmpty(restored))
        {
            Password = restored;
            RememberPassword = true;
            StatusMessage = "Saved server loaded with stored password — Connect when ready.";
        }
        else
        {
            Password = string.Empty;
            StatusMessage = "Saved server loaded — enter password and Connect.";
        }
    }

    [RelayCommand]
    private void ForgetSavedPassword(SavedServerEntry? entry)
    {
        if (entry is null || !entry.HasSavedPassword)
            return;

        var address = entry.Address;
        var username = entry.Username;
        for (var i = SavedServers.Count - 1; i >= 0; i--)
        {
            if (string.Equals(SavedServers[i].Address, address, StringComparison.OrdinalIgnoreCase))
                SavedServers.RemoveAt(i);
        }

        SavedServers.Insert(0, new SavedServerEntry(address, username, EncryptedPassword: null));
        PersistSavedServers();
        if (string.Equals(HostInput, address, StringComparison.OrdinalIgnoreCase))
            Password = string.Empty;
        StatusMessage = "Saved password forgotten.";
    }

    [RelayCommand]
    private void ClearTrustedCertificates()
    {
        if (ShellBootstrap.CertificateStore.Count == 0)
        {
            StatusMessage = "No trusted certificates to clear.";
            RefreshTrustUi();
            return;
        }

        ShellBootstrap.CertificateStore.Clear();
        RefreshTrustUi();
        StatusMessage = "Trusted certificates cleared. Next connect will prompt again.";
    }

    [RelayCommand(CanExecute = nameof(CanDisconnectSelected))]
    private void DisconnectSelected()
    {
        var server = SelectedInfraNode?.Server ?? SelectedServer;
        if (server?.Connection is null)
            return;

        StopConsoleIfBoundTo(server);

        var conn = server.Connection;
        DetachAlertsForConnection(conn);
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
        ForgetServer(server.Address);
        SelectedServer = Servers.Count > 0 ? Servers[0] : null;
        if (InfrastructureRoots.Count == 0)
        {
            _pinnedInfraNode = null;
            SelectedInfraNode = null;
            RefreshDetailPanes();
        }
        else
        {
            SelectInfraNode(InfrastructureRoots[0]);
        }

        StatusMessage = Servers.Count == 0 ? string.Empty : "Server removed.";
    }

    private void LoadSavedServers()
    {
        SavedServers.Clear();
        foreach (var entry in _savedServerStore.Load())
            SavedServers.Add(entry);
    }

    private void RememberServer(string address, string username, string? password)
    {
        var existing = SavedServers.FirstOrDefault(s =>
            string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));
        var previousSecret = existing?.EncryptedPassword;
        if (existing != null)
            SavedServers.Remove(existing);

        string? encrypted = null;
        if (!string.IsNullOrEmpty(password))
            encrypted = SavedServerStore.ProtectPassword(password);
        else if (RememberPassword)
            encrypted = previousSecret;

        SavedServers.Insert(0, new SavedServerEntry(address, username, encrypted));
        PersistSavedServers();
    }

    private void ForgetServer(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return;
        for (var i = SavedServers.Count - 1; i >= 0; i--)
        {
            if (string.Equals(SavedServers[i].Address, address, StringComparison.OrdinalIgnoreCase))
                SavedServers.RemoveAt(i);
        }
        PersistSavedServers();
    }

    private void PersistSavedServers()
        => _savedServerStore.Save(SavedServers);

    private void RefreshTrustUi()
    {
        OnPropertyChanged(nameof(HasTrustedCertificates));
        OnPropertyChanged(nameof(ClearPinsLabel));
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
            DetachAlertsForConnection(node.Connection);
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
            RefreshTrustUi();
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

        AttachAlertsForConnection(conn);
        RebuildTreeForServer(node, conn, selectRoot: true);
    }

    private void OnConnectionClosed(ServerNode node)
    {
        if (node.IsConnecting)
            return;

        DetachAlertsForConnection(node.Connection);
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

        var selectedRef = _pinnedInfraNode?.Server == server
            ? _pinnedInfraNode.OpaqueRef
            : SelectedInfraNode?.Server == server
                ? SelectedInfraNode.OpaqueRef
                : null;

        var root = InfrastructureTreeBuilder.Build(server, conn);

        TreeLayoutChanging?.Invoke();
        _suppressSelectionClear = true;
        try
        {
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

            if (selectRoot || _pinnedInfraNode?.Server == server || SelectedInfraNode?.Server == server)
            {
                var next = FindByOpaqueRef(root, selectedRef) ?? root;
                SelectInfraNode(next);
            }
        }
        finally
        {
            _suppressSelectionClear = false;
            Dispatcher.UIThread.Post(() => TreeLayoutChanged?.Invoke(), DispatcherPriority.Loaded);
        }

        RefreshDetailPanes();
    }

    private void RefreshDetailPanes()
    {
        var node = SelectedInfraNode ?? _pinnedInfraNode;
        RefreshSelectedVm();
        RefreshGeneralProperties(node);
        RefreshStorageProperties(node);
        RefreshNetworkProperties(node);
        RefreshConsoleProperties(node);
        RefreshSnapshotProperties();
        RefreshPerformanceProperties(node);
    }

    private void RefreshGeneralProperties(InfraTreeNode? node)
    {
        GeneralProperties.Clear();
        PropertyCopyFeedback = string.Empty;
        foreach (var row in GeneralSummaryBuilder.Build(node))
            GeneralProperties.Add(row);
    }

    private void RefreshStorageProperties(InfraTreeNode? node)
    {
        StorageTotals.Clear();
        StorageItems.Clear();
        var summary = StorageSummaryBuilder.Build(node);
        foreach (var row in summary.Totals)
            StorageTotals.Add(row);
        foreach (var item in summary.Items)
            StorageItems.Add(item);
        HasStorageItems = StorageItems.Count > 0;
    }

    private void RefreshNetworkProperties(InfraTreeNode? node)
    {
        NetworkTotals.Clear();
        NetworkItems.Clear();
        NetworkMgmtItems.Clear();
        var summary = NetworkSummaryBuilder.Build(node);
        foreach (var row in summary.Totals)
            NetworkTotals.Add(row);
        foreach (var item in summary.Networks)
            NetworkItems.Add(item);
        foreach (var item in summary.Management)
            NetworkMgmtItems.Add(item);
        HasNetworkItems = NetworkItems.Count > 0;
        HasNetworkMgmtItems = NetworkMgmtItems.Count > 0;
        OnPropertyChanged(nameof(HasAnyNetworkContent));
    }

    private void RefreshConsoleProperties(InfraTreeNode? node)
    {
        ConsoleTotals.Clear();
        ConsoleItems.Clear();
        ConsoleCopyFeedback = string.Empty;
        var summary = ConsoleSummaryBuilder.Build(node);
        foreach (var row in summary.Totals)
            ConsoleTotals.Add(row);
        foreach (var item in summary.Items)
            ConsoleItems.Add(item);
        HasConsoleItems = ConsoleItems.Count > 0;
        ConsoleStatusMessage = summary.StatusMessage;
        ConsolePlaceholderMessage = summary.PlaceholderMessage;
        SyncLiveConsole(summary.LiveTarget);
    }

    private void RefreshSnapshotProperties()
    {
        SnapshotItems.Clear();
        SnapshotStatusMessage = string.Empty;
        CanManageSnapshots = SelectedVm is { is_a_template: false, is_a_snapshot: false, is_control_domain: false };
        if (!CanManageSnapshots)
        {
            HasSnapshotItems = false;
            return;
        }

        foreach (var row in SnapshotSummaryBuilder.Build(SelectedVm))
            SnapshotItems.Add(row);

        HasSnapshotItems = SelectedVm!.snapshots is { Count: > 0 };
        if (!HasSnapshotItems)
            SnapshotStatusMessage = "No snapshots yet — take one to start a tree.";
    }

    private void SyncLiveConsole(LiveRfbTarget? target)
    {
        if (target is not { } live)
        {
            if (_activeConsoleKey != null)
            {
                _activeConsoleKey = null;
                CloseConsolePopOut();
                _consoleSession.Stop();
                ConsoleBitmap = null;
                ConsoleViewerStatus = string.Empty;
                ConsoleInputHint = string.Empty;
                IsConsoleConnecting = false;
            }
            return;
        }

        var key = $"{live.Connection.Hostname}|{live.Console.opaque_ref}|{live.Console.location}";
        if (key == _activeConsoleKey)
            return;

        CloseConsolePopOut();
        _activeConsoleKey = key;
        ConsoleBitmap = null;
        IsConsoleConnecting = true;
        ConsoleViewerStatus = "Connecting to RFB console…";
        ConsoleInputHint = string.Empty;
        _consoleSession.Start(live);
    }

    private void OnConsoleSessionStateChanged()
    {
        var bitmap = _consoleSession.Bitmap;
        if (!ReferenceEquals(ConsoleBitmap, bitmap))
            ConsoleBitmap = bitmap;
        else
            OnPropertyChanged(nameof(ConsoleBitmap));

        ConsoleViewerStatus = _consoleSession.StatusMessage;
        IsConsoleConnecting = !_consoleSession.IsConnected
                              && !string.IsNullOrEmpty(_consoleSession.StatusMessage)
                              && _consoleSession.StatusMessage.Contains("Connecting", StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(HasConsoleFrame));
        OnPropertyChanged(nameof(ShowEmbeddedConsole));
        if (!HasConsoleFrame)
            ConsoleInputHint = string.Empty;
        else if (string.IsNullOrEmpty(ConsoleInputHint))
            ConsoleInputHint = "Click the console to send keyboard and mouse to the guest.";
    }

    [RelayCommand]
    private async Task CopyConsoleLocationAsync(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return;
        ConsoleCopyFeedback = await TryCopyTextAsync(location)
            ? "Console location copied."
            : "Clipboard unavailable.";
    }

    [RelayCommand]
    private async Task CopyPropertyValueAsync(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        PropertyCopyFeedback = await TryCopyTextAsync(value)
            ? "Copied."
            : "Clipboard unavailable.";
    }

    private static async Task<bool> TryCopyTextAsync(string text)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow: { Clipboard: { } clipboard }
                })
            {
                await clipboard.SetTextAsync(text);
                return true;
            }
        }
        catch
        {
            // Clipboard failures are surfaced via feedback text.
        }

        return false;
    }

    private void RemoveTreeForServer(ServerNode server)
    {
        StopConsoleIfBoundTo(server);

        _suppressSelectionClear = true;
        try
        {
            for (var i = InfrastructureRoots.Count - 1; i >= 0; i--)
            {
                if (InfrastructureRoots[i].Server == server)
                    InfrastructureRoots.RemoveAt(i);
            }
        }
        finally
        {
            _suppressSelectionClear = false;
        }

        if (_pinnedInfraNode?.Server == server || SelectedInfraNode?.Server == server)
            ClearSelectionIfPinnedTo(server);
    }

    private void StopConsoleIfBoundTo(ServerNode server)
    {
        var affectsSelection = _pinnedInfraNode?.Server == server || SelectedInfraNode?.Server == server;
        var host = server.Connection?.Hostname ?? server.Address;
        var affectsKey = _activeConsoleKey != null
                         && !string.IsNullOrEmpty(host)
                         && _activeConsoleKey.StartsWith(host + "|", StringComparison.OrdinalIgnoreCase);

        if (!affectsSelection && !affectsKey)
            return;

        _activeConsoleKey = null;
        _consoleSession.Stop();
        ConsoleBitmap = null;
        ConsoleViewerStatus = string.Empty;
        ConsoleInputHint = string.Empty;
        IsConsoleConnecting = false;
    }

    private void ClearSelectionIfPinnedTo(ServerNode server)
    {
        if (_pinnedInfraNode?.Server != server && SelectedInfraNode?.Server != server)
            return;

        var fallback = InfrastructureRoots.FirstOrDefault();
        if (fallback != null)
        {
            SelectInfraNode(fallback);
            return;
        }

        _pinnedInfraNode = null;
        SelectedInfraNode = null;
        RefreshDetailPanes();
    }

    private void SelectInfraNode(InfraTreeNode node)
    {
        _pinnedInfraNode = node;
        SelectedServer = node.Server;
        if (!ReferenceEquals(SelectedInfraNode, node))
            SelectedInfraNode = node;
        else
            RefreshDetailPanes();
    }

    private void QueueRestorePinnedSelection()
    {
        if (_restoreSelectionQueued || _pinnedInfraNode == null)
            return;

        _restoreSelectionQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _restoreSelectionQueued = false;
            if (SelectedInfraNode != null || _pinnedInfraNode == null || !HasServers)
                return;

            // Keep scroll when re-asserting the pin after Avalonia clears SelectedItem.
            TreeLayoutChanging?.Invoke();
            try
            {
                var pinned = _pinnedInfraNode;
                var live = pinned.Server != null
                    ? InfrastructureRoots.FirstOrDefault(r => r.Server == pinned.Server)
                    : null;
                var restored = live != null
                    ? FindByOpaqueRef(live, pinned.OpaqueRef) ?? live
                    : pinned;

                SelectInfraNode(restored);
            }
            finally
            {
                Dispatcher.UIThread.Post(() => TreeLayoutChanged?.Invoke(), DispatcherPriority.Loaded);
            }
        });
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
