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
using XcpNgCenter.Shell.Views;

namespace XcpNgCenter.Shell.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly HostedConsoleSession _consoleSession = new();
    private readonly SavedServerStore _savedServerStore = new();
    private readonly ShellAppSettings _appSettings = ShellBootstrap.AppSettings;
    private bool _disposed;
    private CancellationTokenSource? _autoReconnectCts;

    /// <summary>In-session main-password hash (for EncryptString) when unlocked.</summary>
    private byte[]? _sessionMainPasswordHash;

    /// <summary>In-session plaintext main password (for DecryptString) when unlocked.</summary>
    private string? _sessionMainPasswordPlain;

    public string BrandName => "XCP-ng Center";

    public string VersionDisplay => ShellVersionInfo.TagDisplay;

    public string Tagline => "Manage pools, hosts, and VMs with a calmer console.";

    public HostedConsoleSession ConsoleSession => _consoleSession;

    public bool FillPerformanceGraphAreas => _appSettings.FillPerformanceGraphAreas;

    public bool ScaleConsoleToFit => _appSettings.ScaleConsoleToFit;

    public string ConsoleReleaseShortcut => _appSettings.ConsoleReleaseShortcut;

    public string ConsoleFullscreenShortcut => _appSettings.ConsoleFullscreenShortcut;

    public string ConsoleDockShortcut => _appSettings.ConsoleDockShortcut;

    public bool ShowLogTimestamps => _appSettings.ShowTimestampsInLogs;

    /// <summary>Passwords can be remembered on all platforms (DPAPI on Windows; AES key file elsewhere).</summary>
    public bool CanPersistPasswords =>
        SavedServerStore.CanPersistPasswords && _appSettings.RememberSavedServers;

    public string RememberPasswordLabel => "Remember password for this server";

    public string PasswordVaultHint => OperatingSystem.IsWindows()
        ? "Passwords are encrypted with Windows DPAPI for the current user when remembered."
        : "Passwords are encrypted with a per-user key file under ~/.config/XCP-ng/XCP-ng Center Shell/.";

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
    [NotifyPropertyChangedFor(nameof(ShowDetailNetwork))]
    [NotifyPropertyChangedFor(nameof(ShowDetailConsole))]
    [NotifyPropertyChangedFor(nameof(ShowDetailSnapshots))]
    [NotifyPropertyChangedFor(nameof(ShowDetailPerformance))]
    private InfraTreeNode? _selectedInfraNode;

    [ObservableProperty]
    private int _selectedDetailTabIndex;

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

    /// <summary>SR selection hides VM/host-only detail tabs.</summary>
    public bool IsStorageSelection =>
        (SelectedInfraNode ?? _pinnedInfraNode)?.Kind == InfraNodeKind.Storage;

    public bool ShowDetailGeneral => true;
    public bool ShowDetailStorage => true;
    public bool ShowDetailLogs => true;
    public bool ShowDetailNetwork => !IsStorageSelection;
    public bool ShowDetailConsole => !IsStorageSelection;
    public bool ShowDetailSnapshots => !IsStorageSelection;
    public bool ShowDetailPerformance => !IsStorageSelection;

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

    public ObservableCollection<SnapshotTypeOption> SnapshotTypeOptions { get; } = new();

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
    [NotifyPropertyChangedFor(nameof(CanTakeSnapshot))]
    [NotifyPropertyChangedFor(nameof(SnapshotTypeHint))]
    [NotifyCanExecuteChangedFor(nameof(TakeSnapshotCommand))]
    private SnapshotTypeOption? _selectedSnapshotType;

    public bool CanTakeSnapshot =>
        CanManageSnapshots && SelectedVm is { Locked: false } && SelectedSnapshotType != null;

    public string SnapshotTypeHint => SelectedSnapshotType?.Detail ??
                                      "No snapshot mode is currently allowed for this VM.";

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
    private DateTime _nextGuestConsoleRetryUtc;

    /// <summary>Raised around infrastructure tree rebuilds/selection restores so the view can keep scroll position.</summary>
    public event Action? TreeLayoutChanging;

    public event Action? TreeLayoutChanged;

    public MainViewModel()
    {
        // Opt-in: do not default RememberPassword just because the platform can persist.
        IdentifierPrivacy.Bind(_appSettings);
        _selectedDetailTabIndex = _appSettings.RememberLastSelectedTab
            ? Math.Clamp(_appSettings.LastSelectedDetailTab, 0, 6)
            : 0;
        _appSettings.Changed += OnAppSettingsChanged;
        Servers.CollectionChanged += (_, _) => HasServers = Servers.Count > 0;
        SavedServers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSavedServers));
        _consoleSession.StateChanged += OnConsoleSessionStateChanged;
        LoadSavedServers();
        RefreshTrustUi();
        InitializeActionHistoryUi();
        InitializeAlertsAndGraphsUi();
        InitializeUpdateCheck();
        if (!string.IsNullOrWhiteSpace(ShellUpdateInstaller.StartupStatusMessage))
            StatusMessage = ShellUpdateInstaller.StartupStatusMessage;
        _ = UnlockMainPasswordThenAutoReconnectAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _autoReconnectCts?.Cancel();
            _autoReconnectCts?.Dispose();
        }
        catch
        {
            // Best-effort.
        }

        _autoReconnectCts = null;

        DisposeUpdateCheck();
        DisposeAlertsAndGraphsUi();
        DisposeActionHistoryUi();
        _appSettings.Changed -= OnAppSettingsChanged;
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
            ? $"Keyboard and mouse captured by guest — move the pointer away, press {ConsoleReleaseShortcut}, or click elsewhere to release."
            : "Click the console to send keyboard and mouse to the guest.";
    }

    partial void OnHostInputChanged(string value)
    {
        ShowPublicIpWarning = ShouldWarnForPublicIp(value);
        AcknowledgePublicIp = false;
    }

    public bool ShouldWarnForPublicIp(string hostInput)
    {
        return _appSettings.WarnPublicIpConnections
               && HostnameAddressClassifier.TryParseHostPort(hostInput.Trim(), out var host, out _)
               && HostnameAddressClassifier.IsPublicIp(host);
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
        NotifyDetailTabVisibility();
        RefreshDetailPanes();
    }

    [RelayCommand]
    private void Connect()
    {
        var error = TryBeginConnect(
            HostInput,
            Username,
            Password,
            RememberPassword,
            publicIpAcknowledged: AcknowledgePublicIp);
        if (error != null)
            StatusMessage = error;
    }

    /// <summary>
    /// Starts a live connect. Returns an error message, or null when the connect was queued.
    /// </summary>
    public string? TryBeginConnect(
        string hostInput,
        string usernameInput,
        string password,
        bool rememberPassword,
        bool publicIpAcknowledged = false,
        bool skipPublicIpWarning = false)
    {
        var raw = hostInput.Trim();
        if (string.IsNullOrEmpty(raw))
            return "Enter a hostname or IP address.";

        if (!HostnameAddressClassifier.TryParseHostPort(raw, out var host, out var port))
            return "That does not look like a valid host.";

        if (string.IsNullOrWhiteSpace(usernameInput))
            return "Enter a username.";

        var isPublic = HostnameAddressClassifier.IsPublicIp(host);
        if (isPublic
            && _appSettings.WarnPublicIpConnections
            && !skipPublicIpWarning
            && !publicIpAcknowledged)
            return "Acknowledge the public-IP warning before connecting.";

        var effectivePort = port > 0 ? port : ConnectionsManager.DEFAULT_XEN_PORT;
        var display = port > 0 ? $"{host}:{port}" : host;
        var username = usernameInput.Trim();

        // Reuse an existing disconnected entry for the same address instead of stacking duplicates.
        var existing = Servers.FirstOrDefault(s =>
            string.Equals(s.Address, display, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(s.Hostname, host, StringComparison.OrdinalIgnoreCase) && s.Port == effectivePort));

        if (existing is { IsConnected: true } or { IsConnecting: true })
            return $"Already connected to {display}.";

        if (existing != null)
        {
            existing.Username = username;
            existing.Password = password;
            existing.RememberPassword = rememberPassword && CanPersistPasswords;
            existing.IsPublicIp = isPublic;
            HostInput = string.Empty;
            ShowPublicIpWarning = false;
            AcknowledgePublicIp = false;
            Password = string.Empty;
            return BeginReconnect(existing);
        }

        var node = new ServerNode
        {
            Name = host,
            Address = display,
            Hostname = host,
            Port = effectivePort,
            Status = "Connecting…",
            Summary = "Connecting…",
            IsPublicIp = isPublic,
            IsConnecting = true,
            Username = username,
            Password = password,
            RememberPassword = rememberPassword && CanPersistPasswords
        };

        Servers.Add(node);
        SelectedServer = node;
        HostInput = string.Empty;
        ShowPublicIpWarning = false;
        AcknowledgePublicIp = false;
        StatusMessage = $"Connecting to {display}…";
        IsBusy = true;
        Password = string.Empty;

        EnsureServerTreePlaceholder(node, select: true);
        BeginLiveConnect(node, host, effectivePort, username, password);
        return null;
    }

    /// <summary>Reconnect a disconnected (or failed) server already on the list.</summary>
    public string? BeginReconnect(ServerNode node)
    {
        if (node.IsConnected || node.IsConnecting)
            return $"Already connected to {node.Address}.";

        var password = node.Password;
        if (string.IsNullOrEmpty(password))
        {
            var saved = SavedServers.FirstOrDefault(s =>
                string.Equals(s.Address, node.Address, StringComparison.OrdinalIgnoreCase));
            password = SavedServerStore.UnprotectPassword(saved?.EncryptedPassword);
        }

        if (string.IsNullOrEmpty(password))
            return "Enter a password and Connect, or use a saved password for this server.";

        if (string.IsNullOrWhiteSpace(node.Hostname))
        {
            if (!HostnameAddressClassifier.TryParseHostPort(node.Address, out var host, out var port))
                return "That does not look like a valid host.";
            node.Hostname = host;
            node.Port = port > 0 ? port : ConnectionsManager.DEFAULT_XEN_PORT;
        }

        TearDownConnection(node, removeFromManager: true);

        node.IsConnecting = true;
        node.IsConnected = false;
        node.Status = "Connecting…";
        node.Summary = "Connecting…";
        node.Password = password;
        SelectedServer = node;
        StatusMessage = $"Connecting to {node.Address}…";
        IsBusy = true;

        EnsureServerTreePlaceholder(node, select: true);
        BeginLiveConnect(node, node.Hostname, node.Port > 0 ? node.Port : ConnectionsManager.DEFAULT_XEN_PORT,
            node.Username, password);
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

        var restored = TryUnprotectSavedPassword(entry.EncryptedPassword);
        if (!string.IsNullOrEmpty(restored))
        {
            Password = restored;
            RememberPassword = true;
            // Prefer signing in immediately when a password is available.
            var error = TryBeginConnect(
                entry.Address,
                entry.Username,
                restored,
                rememberPassword: true,
                skipPublicIpWarning: true);
            StatusMessage = error ?? $"Connecting to {entry.Address}…";
            if (error == null)
                Password = string.Empty;
        }
        else if (SavedServerStore.IsMainPasswordProtected(entry.EncryptedPassword)
                 && string.IsNullOrEmpty(_sessionMainPasswordPlain))
        {
            Password = string.Empty;
            StatusMessage = "Main password required to unlock this saved password.";
        }
        else
        {
            Password = string.Empty;
            StatusMessage = "Saved server loaded — enter password and Connect.";
        }
    }

    private async Task UnlockMainPasswordThenAutoReconnectAsync()
    {
        if (_appSettings.RequireMainPassword)
        {
            var hash = _appSettings.GetMainPasswordHash();
            if (hash != null)
            {
                var unlocked = await PromptEnterMainPasswordAsync(
                    hash,
                    title: "Unlock saved credentials",
                    message: "Enter the main password to reconnect your servers.").ConfigureAwait(true);

                if (!unlocked)
                {
                    StatusMessage = "Main password not entered — saved passwords were not unlocked.";
                    return;
                }
            }
        }

        QueueAutoReconnectSavedServers();
    }

    private static Avalonia.Controls.Window? GetDesktopMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    public void NotifyPrivacyChanged()
    {
        // Refresh saved-server display bindings (DisplayAddress).
        var snapshot = SavedServers.ToList();
        SavedServers.Clear();
        foreach (var entry in snapshot)
            SavedServers.Add(entry);

        foreach (var root in InfrastructureRoots.ToList())
        {
            if (root.Server?.Connection is { IsConnected: true } conn)
                RebuildTreeForServer(root.Server, conn);
        }

        RefreshDetailPanes();
    }

    public void PersistCurrentDetailTabPreference()
    {
        if (_appSettings.RememberLastSelectedTab)
            _appSettings.LastSelectedDetailTab = SelectedDetailTabIndex;
    }

    partial void OnSelectedDetailTabIndexChanged(int value)
    {
        if (_appSettings.RememberLastSelectedTab && value is >= 0 and <= 6)
            _appSettings.LastSelectedDetailTab = value;
    }

    private void OnAppSettingsChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(FillPerformanceGraphAreas));
            OnPropertyChanged(nameof(ScaleConsoleToFit));
            OnPropertyChanged(nameof(ConsoleReleaseShortcut));
            OnPropertyChanged(nameof(ConsoleFullscreenShortcut));
            OnPropertyChanged(nameof(ConsoleDockShortcut));
            OnPropertyChanged(nameof(ShowLogTimestamps));
            OnPropertyChanged(nameof(CanPersistPasswords));
            ShowPublicIpWarning = ShouldWarnForPublicIp(HostInput);
            if (!ShowPublicIpWarning)
                AcknowledgePublicIp = false;
        });
    }

    public void SetSessionMainPassword(byte[] hash, string? plain = null)
    {
        _sessionMainPasswordHash = hash;
        if (plain != null)
            _sessionMainPasswordPlain = plain;
    }

    public void ClearSessionMainPassword()
    {
        _sessionMainPasswordHash = null;
        _sessionMainPasswordPlain = null;
    }

    private string? TryUnprotectSavedPassword(string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted))
            return null;

        if (SavedServerStore.IsMainPasswordProtected(encrypted))
        {
            if (string.IsNullOrEmpty(_sessionMainPasswordPlain))
                return null;
            return SavedServerStore.UnprotectPasswordWithMainPassword(encrypted, _sessionMainPasswordPlain);
        }

        return SavedServerStore.UnprotectPassword(encrypted);
    }

    private string? ProtectSavedPassword(string password)
    {
        if (_appSettings.RequireMainPassword)
        {
            if (_sessionMainPasswordHash == null)
                return null;
            return SavedServerStore.ProtectPasswordWithMainPassword(password, _sessionMainPasswordHash);
        }

        return SavedServerStore.ProtectPassword(password);
    }

    public async Task<bool> PromptEnterMainPasswordAsync(
        byte[] expectedHash,
        string? title = null,
        string? message = null)
    {
        var owner = await WaitForMainWindowAsync().ConfigureAwait(true);
        var dialog = new EnterMainPasswordWindow(expectedHash, title, message);
        if (owner == null)
            return false;

        var result = await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
        if (!result)
            return false;

        _sessionMainPasswordHash = expectedHash;
        _sessionMainPasswordPlain = dialog.Password;
        return true;
    }

    public async Task<(byte[] Hash, string Plain)?> PromptSetMainPasswordAsync()
    {
        var owner = await WaitForMainWindowAsync().ConfigureAwait(true);
        if (owner == null)
            return null;

        var dialog = new SetMainPasswordWindow();
        var result = await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
        if (!result || dialog.NewPasswordHash == null || string.IsNullOrEmpty(dialog.PasswordPlain))
            return null;
        return (dialog.NewPasswordHash, dialog.PasswordPlain);
    }

    public async Task<(byte[] Hash, string CurrentPlain, string NewPlain)?> PromptChangeMainPasswordAsync(byte[] currentHash)
    {
        var owner = await WaitForMainWindowAsync().ConfigureAwait(true);
        if (owner == null)
            return null;

        var dialog = new ChangeMainPasswordWindow(currentHash);
        var result = await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
        if (!result
            || dialog.NewPasswordHash == null
            || string.IsNullOrEmpty(dialog.CurrentPasswordPlain)
            || string.IsNullOrEmpty(dialog.NewPasswordPlain))
            return null;
        return (dialog.NewPasswordHash, dialog.CurrentPasswordPlain, dialog.NewPasswordPlain);
    }

    private static async Task<Avalonia.Controls.Window?> WaitForMainWindowAsync()
    {
        for (var i = 0; i < 80; i++)
        {
            var window = GetDesktopMainWindow();
            // Prefer MainWindow once splash has been replaced.
            if (window is MainWindow)
                return window;
            await Task.Delay(100).ConfigureAwait(true);
        }

        return GetDesktopMainWindow();
    }

    public Task MigrateSavedPasswordsToMainPasswordAsync(byte[] hash)
    {
        for (var i = 0; i < SavedServers.Count; i++)
        {
            var entry = SavedServers[i];
            if (!entry.HasSavedPassword)
                continue;

            var plain = TryUnprotectSavedPassword(entry.EncryptedPassword);
            if (string.IsNullOrEmpty(plain))
                continue;

            var wrapped = SavedServerStore.ProtectPasswordWithMainPassword(plain, hash);
            if (wrapped == null)
                continue;

            SavedServers[i] = entry with { EncryptedPassword = wrapped };
        }

        PersistSavedServers();
        return Task.CompletedTask;
    }

    public Task MigrateSavedPasswordsFromMainPasswordAsync()
    {
        for (var i = 0; i < SavedServers.Count; i++)
        {
            var entry = SavedServers[i];
            if (!entry.HasSavedPassword)
                continue;

            var plain = TryUnprotectSavedPassword(entry.EncryptedPassword);
            if (string.IsNullOrEmpty(plain))
                continue;

            var device = SavedServerStore.ProtectPassword(plain);
            SavedServers[i] = entry with { EncryptedPassword = device };
        }

        PersistSavedServers();
        return Task.CompletedTask;
    }

    public Task ReencryptSavedPasswordsForMainPasswordChangeAsync(
        string currentPlain,
        byte[] newHash)
    {
        for (var i = 0; i < SavedServers.Count; i++)
        {
            var entry = SavedServers[i];
            if (!entry.HasSavedPassword)
                continue;

            string? plain = null;
            if (SavedServerStore.IsMainPasswordProtected(entry.EncryptedPassword))
                plain = SavedServerStore.UnprotectPasswordWithMainPassword(entry.EncryptedPassword, currentPlain);
            else
                plain = SavedServerStore.UnprotectPassword(entry.EncryptedPassword);

            if (string.IsNullOrEmpty(plain))
                continue;

            var wrapped = SavedServerStore.ProtectPasswordWithMainPassword(plain, newHash);
            if (wrapped == null)
                continue;

            SavedServers[i] = entry with { EncryptedPassword = wrapped };
        }

        PersistSavedServers();
        return Task.CompletedTask;
    }

    private void QueueAutoReconnectSavedServers()
    {
        if (!_appSettings.AutoReconnectSavedServers)
            return;

        var candidates = SavedServers
            .Where(s => s.HasSavedPassword && !string.IsNullOrWhiteSpace(s.Address))
            .ToList();
        if (candidates.Count == 0)
            return;

        _autoReconnectCts?.Cancel();
        _autoReconnectCts?.Dispose();
        _autoReconnectCts = new CancellationTokenSource();
        var token = _autoReconnectCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // Let the main window paint before opening connections / TOFU prompts.
                await Task.Delay(900, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || !_appSettings.AutoReconnectSavedServers)
                    return;
                AutoReconnectSavedServers(candidates);
            });
        }, token);
    }

    private void AutoReconnectSavedServers(IReadOnlyList<SavedServerEntry> candidates)
    {
        var started = 0;
        foreach (var entry in candidates)
        {
            var password = TryUnprotectSavedPassword(entry.EncryptedPassword);
            if (string.IsNullOrEmpty(password))
                continue;

            var error = TryBeginConnect(
                entry.Address,
                entry.Username,
                password,
                rememberPassword: true,
                skipPublicIpWarning: true);
            if (error == null)
                started++;
        }

        if (started > 0)
            StatusMessage = started == 1
                ? "Reconnecting saved server…"
                : $"Reconnecting {started} saved servers…";
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            {
                MainWindow: { } owner
            })
            return;

        var dialog = new SettingsWindow(this, _appSettings);
        await dialog.ShowDialog(owner);
        RefreshTrustUi();
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
        StatusMessage = "Trusted certificates cleared. Servers will be treated as first seen on their next connection.";
    }

    [RelayCommand(CanExecute = nameof(CanDisconnectSelected))]
    private void DisconnectSelected()
    {
        var server = SelectedInfraNode?.Server ?? SelectedServer;
        if (server is null)
            return;

        if (server.IsConnecting)
        {
            CancelConnectSelected();
            return;
        }

        if (server.Connection is null && server.IsDisconnected)
            return;

        StopConsoleIfBoundTo(server);
        TearDownConnection(server, removeFromManager: true);

        server.IsConnected = false;
        server.IsConnecting = false;
        server.Status = "Disconnected";
        server.Summary = "Disconnected — right-click to reconnect";
        EnsureServerTreePlaceholder(server, select: true);
        StatusMessage = "Disconnected.";
        IsBusy = false;
        NotifyServerActionCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCancelConnectSelected))]
    private void CancelConnectSelected()
    {
        var server = SelectedInfraNode?.Server ?? SelectedServer;
        if (server is not { IsConnecting: true })
            return;

        StopConsoleIfBoundTo(server);
        TearDownConnection(server, removeFromManager: true);

        server.IsConnecting = false;
        server.IsConnected = false;
        server.Status = "Cancelled";
        server.Summary = "Connection cancelled — right-click to reconnect";
        EnsureServerTreePlaceholder(server, select: true);
        StatusMessage = "Connection cancelled.";
        IsBusy = false;
        NotifyServerActionCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanReconnectSelected))]
    private void ReconnectSelected()
    {
        var server = SelectedInfraNode?.Server ?? SelectedServer;
        if (server is null)
            return;

        var error = BeginReconnect(server);
        if (error != null)
            StatusMessage = error;
        NotifyServerActionCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private void RemoveSelected()
    {
        var server = SelectedInfraNode?.Server ?? SelectedServer;
        if (server is null)
            return;

        StopConsoleIfBoundTo(server);
        TearDownConnection(server, removeFromManager: true);

        RemoveTreeForServer(server);
        Servers.Remove(server);
        ForgetServer(server.Address);
        server.Password = null;
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
        IsBusy = false;
        NotifyServerActionCanExecuteChanged();
    }

    private void LoadSavedServers()
    {
        SavedServers.Clear();
        if (!_appSettings.RememberSavedServers)
        {
            _savedServerStore.Save([]);
            return;
        }
        foreach (var entry in _savedServerStore.Load())
            SavedServers.Add(entry);
    }
    private void RememberServer(string address, string username, string? password)
    {
        if (!_appSettings.RememberSavedServers)
            return;

        var existing = SavedServers.FirstOrDefault(s =>
            string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));
        var previousSecret = existing?.EncryptedPassword;
        if (existing != null)
            SavedServers.Remove(existing);

        string? encrypted = null;
        if (!string.IsNullOrEmpty(password))
        {
            encrypted = ProtectSavedPassword(password);
            if (encrypted == null)
            {
                // Do not wipe an existing protected blob when main password is locked.
                encrypted = previousSecret;
                if (_appSettings.RequireMainPassword && _sessionMainPasswordHash == null)
                    StatusMessage = "Unlock the main password to update saved credentials.";
            }
        }
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
        => _savedServerStore.Save(_appSettings.RememberSavedServers ? SavedServers : []);

    public void SetRememberSavedServers(bool enabled)
    {
        _appSettings.RememberSavedServers = enabled;
        if (enabled)
            return;

        RememberPassword = false;
        SavedServers.Clear();
        _savedServerStore.Save([]);
        OnPropertyChanged(nameof(CanPersistPasswords));
    }

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
        node.Hostname = host;
        node.Port = port;

        conn.ConnectionResult += (_, e) => Dispatcher.UIThread.Post(() => OnConnectionResult(node, e));
        conn.CachePopulated += c => Dispatcher.UIThread.Post(() => OnCachePopulated(node, c));
        conn.XenObjectsUpdated += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // Require both the Shell flag and XenConnection.IsConnected so a queued
            // refresh after coordinator death cannot rebuild a green "online" tree from
            // stale Host_metrics.live snapshots.
            if (node.Connection != null && node.IsConnected && node.Connection.IsConnected)
                RebuildTreeForServer(node, node.Connection);
        });
        conn.ClearingCache += c => Dispatcher.UIThread.Post(() => OnConnectionClearingCache(node, c));
        conn.ConnectionStateChanged += c => Dispatcher.UIThread.Post(() => OnConnectionStateChanged(node, c));
        conn.ConnectionClosed += _ => Dispatcher.UIThread.Post(() => OnConnectionClosed(node));
        conn.ConnectionLost += _ => Dispatcher.UIThread.Post(() => OnConnectionLost(node));
        conn.ConnectionReconnecting += _ => Dispatcher.UIThread.Post(() =>
        {
            if (!Servers.Contains(node))
                return;
            node.IsConnecting = true;
            node.IsConnected = false;
            node.Status = "Reconnecting…";
            node.Summary = "Reconnecting…";
            EnsureServerTreePlaceholder(node);
            StatusMessage = $"Reconnecting to {node.Address}…";
            NotifyServerActionCanExecuteChanged();
        });
        conn.ConnectionMessageChanged += (_, msg) => Dispatcher.UIThread.Post(() =>
        {
            if (node.IsConnecting && !string.IsNullOrWhiteSpace(msg))
            {
                node.Status = msg;
                RefreshServerTreePlaceholder(node);
            }
        });

        try
        {
            conn.BeginConnect(initiateCoordinatorSearch: false, PromptForNewPassword);
        }
        catch (Exception ex)
        {
            MarkServerDisconnected(node, "Connect failed", ex.Message);
            StatusMessage = ex.Message;
            IsBusy = false;
            NotifyServerActionCanExecuteChanged();
        }
    }

    private static bool PromptForNewPassword(IXenConnection connection, string oldPassword)
        => false;

    private void OnConnectionResult(ServerNode node, ConnectionResultEventArgs e)
    {
        if (e.Connected)
        {
            node.Status = "Connected — loading inventory…";
            node.Summary = "Loading inventory…";
            RefreshServerTreePlaceholder(node);
            if (!string.IsNullOrEmpty(ShellBootstrap.CertificateValidator.LastMessage))
                StatusMessage = ShellBootstrap.CertificateValidator.LastMessage;
            RefreshTrustUi();
            return;
        }

        IsBusy = false;

        var reason = !string.IsNullOrWhiteSpace(e.Reason)
            ? e.Reason
            : e.Error?.Message ?? "Connection failed.";

        // First-time / failed connect: stop immediately — no silent reattempt.
        // Keep the server listed as disconnected so the user can reconnect or remove it.
        TearDownConnection(node, removeFromManager: true);
        MarkServerDisconnected(node, "Disconnected", reason);
        StatusMessage = reason;
        NotifyServerActionCanExecuteChanged();
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
        node.Hostname = conn.Hostname;
        node.Port = conn.Port;
        node.Status = "Connected";
        node.Summary = $"{hosts} host(s), {vms} VM(s)";
        StatusMessage = $"Connected to {conn.HostnameWithPort}.";

        // Persist only after a successful connect (never on a failed first add).
        RememberServer(
            node.Address,
            node.Username,
            node.RememberPassword && CanPersistPasswords ? node.Password : null);

        AttachAlertsForConnection(conn);
        RebuildTreeForServer(node, conn, selectRoot: true);
        NotifyServerActionCanExecuteChanged();
    }

    /// <summary>
    /// Fired as soon as XenConnection clears its cache (before ConnectionClosed/Lost).
    /// WinForms refreshes the tree here; without it the Shell keeps painting green host
    /// icons from the last Host_metrics.live snapshot while the coordinator is already down.
    /// </summary>
    private void OnConnectionClearingCache(ServerNode node, IXenConnection conn)
    {
        if (!Servers.Contains(node) || !ReferenceEquals(node.Connection, conn))
            return;

        StopConsoleIfBoundTo(node);

        if (!node.IsConnected && !node.IsConnecting)
            return;

        node.IsConnected = false;
        if (!node.IsConnecting)
        {
            node.Status = "Connection lost";
            node.Summary = "Disconnected — right-click to reconnect";
        }

        EnsureServerTreePlaceholder(node);
        NotifyServerActionCanExecuteChanged();
    }

    private void OnConnectionStateChanged(ServerNode node, IXenConnection conn)
    {
        if (!Servers.Contains(node) || !ReferenceEquals(node.Connection, conn))
            return;

        // Drop stale "connected" chrome as soon as XenConnection flips IsConnected.
        if (!conn.IsConnected && node.IsConnected)
        {
            node.IsConnected = false;
            StopConsoleIfBoundTo(node);
            EnsureServerTreePlaceholder(node);
            NotifyServerActionCanExecuteChanged();
        }
    }

    private void OnConnectionClosed(ServerNode node)
    {
        if (!Servers.Contains(node))
            return;

        // TearDownConnection already cleared Connection (e.g. Cancel); keep Cancelled status.
        if (node.Connection is null)
            return;

        // Cancelled in-flight connects / reconnect searches are handled elsewhere.
        if (node.IsConnecting)
            return;

        DetachAlertsForConnection(node.Connection);
        MarkServerDisconnected(node, "Disconnected", "Disconnected — right-click to reconnect");
        IsBusy = false;
        NotifyServerActionCanExecuteChanged();
    }

    private void OnConnectionLost(ServerNode node)
    {
        if (!Servers.Contains(node))
            return;

        DetachAlertsForConnection(node.Connection);
        StopConsoleIfBoundTo(node);

        if (_appSettings.AutoRetryLostConnections && node.Connection != null
            && ConnectionsManager.XenConnectionsContains(node.Connection))
        {
            // Leave the connection registered so XenConnection's reconnect timer can run.
            node.IsConnected = false;
            node.IsConnecting = true;
            node.Status = "Connection lost — retrying…";
            node.Summary = "Will retry automatically";
            EnsureServerTreePlaceholder(node);
            StatusMessage = $"Connection lost to {node.Address}. Retrying…";
        }
        else
        {
            // Default: cancel any pending reconnect and stay disconnected until the user asks.
            TearDownConnection(node, removeFromManager: true);
            MarkServerDisconnected(node, "Connection lost", "Disconnected — right-click to reconnect");
            StatusMessage = "Connection lost.";
        }

        IsBusy = false;
        NotifyServerActionCanExecuteChanged();
    }

    private void MarkServerDisconnected(ServerNode node, string status, string summary)
    {
        node.IsConnecting = false;
        node.IsConnected = false;
        node.Status = status;
        node.Summary = summary;
        EnsureServerTreePlaceholder(node);
    }

    private void TearDownConnection(ServerNode server, bool removeFromManager)
    {
        var conn = server.Connection;
        if (conn is null)
            return;

        DetachAlertsForConnection(conn);
        try
        {
            conn.EndConnect();
        }
        catch
        {
            // Best-effort.
        }

        if (removeFromManager && ConnectionsManager.XenConnectionsContains(conn))
            ConnectionsManager.ClearCacheAndRemoveConnection(conn);

        server.Connection = null;
    }

    private void EnsureServerTreePlaceholder(ServerNode server, bool select = false)
    {
        var root = InfrastructureTreeBuilder.BuildPlaceholder(server);

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

            if (select || _pinnedInfraNode?.Server == server || SelectedInfraNode?.Server == server
                || SelectedServer == server)
            {
                SelectInfraNode(root);
            }
        }
        finally
        {
            _suppressSelectionClear = false;
            TreeLayoutChanged?.Invoke();
        }
    }

    private void RefreshServerTreePlaceholder(ServerNode server)
    {
        var existing = InfrastructureRoots.FirstOrDefault(r => r.Server == server);
        if (existing == null || server.IsConnected)
            return;

        existing.Title = string.IsNullOrWhiteSpace(server.Name) ? server.Address : server.Name;
        existing.Subtitle = server.Address;
        existing.Detail = string.IsNullOrWhiteSpace(server.Summary) ? server.Status : server.Summary;
        var (icon, tip) = server.IsConnecting
            ? (ShellStatusIcons.HostConnecting, "Connecting…")
            : (ShellStatusIcons.HostDisconnected, server.Status);
        existing.ShowStatusIcon = true;
        existing.StatusIcon = icon;
        existing.StatusTooltip = tip;
    }

    private void NotifyServerActionCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanDisconnectSelected));
        OnPropertyChanged(nameof(CanCancelConnectSelected));
        OnPropertyChanged(nameof(CanReconnectSelected));
        OnPropertyChanged(nameof(CanRemoveSelected));
        DisconnectSelectedCommand.NotifyCanExecuteChanged();
        CancelConnectSelectedCommand.NotifyCanExecuteChanged();
        ReconnectSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    private void RebuildTreeForServer(ServerNode server, IXenConnection conn, bool selectRoot = false)
    {
        if (!server.IsConnected
            || !ReferenceEquals(server.Connection, conn)
            || !conn.IsConnected)
        {
            return;
        }

        var selectedRef = _pinnedInfraNode?.Server == server
            ? _pinnedInfraNode.OpaqueRef
            : SelectedInfraNode?.Server == server
                ? SelectedInfraNode.OpaqueRef
                : null;

        var existing = InfrastructureRoots.FirstOrDefault(r => r.Server == server);
        var expandState = existing != null
            ? CaptureExpandState(existing)
            : new Dictionary<string, bool>(StringComparer.Ordinal);

        var root = InfrastructureTreeBuilder.Build(server, conn);
        ApplyExpandState(root, expandState);

        TreeLayoutChanging?.Invoke();
        _suppressSelectionClear = true;
        try
        {
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
                ExpandAncestors(root, next);
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

    private static Dictionary<string, bool> CaptureExpandState(InfraTreeNode root)
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        CaptureExpandStateRecursive(root, map);
        return map;
    }

    private static void CaptureExpandStateRecursive(InfraTreeNode node, Dictionary<string, bool> map)
    {
        var key = ExpandStateKey(node);
        if (key != null)
            map[key] = node.IsExpanded;

        foreach (var child in node.Children)
            CaptureExpandStateRecursive(child, map);
    }

    private static void ApplyExpandState(InfraTreeNode root, IReadOnlyDictionary<string, bool> map)
    {
        ApplyExpandStateRecursive(root, map);
    }

    private static void ApplyExpandStateRecursive(InfraTreeNode node, IReadOnlyDictionary<string, bool> map)
    {
        var key = ExpandStateKey(node);
        if (key != null && map.TryGetValue(key, out var expanded))
            node.IsExpanded = expanded;

        foreach (var child in node.Children)
            ApplyExpandStateRecursive(child, map);
    }

    private static string? ExpandStateKey(InfraTreeNode node)
    {
        if (!string.IsNullOrEmpty(node.OpaqueRef))
            return $"{(int)node.Kind}:{node.OpaqueRef}";

        // Group nodes (e.g. "Other VMs") have no opaque ref — key by kind + title.
        if (node.Kind == InfraNodeKind.Group)
            return $"group:{node.Title}";

        return null;
    }

    private static void ExpandAncestors(InfraTreeNode root, InfraTreeNode target)
    {
        if (ReferenceEquals(root, target))
            return;

        var path = new List<InfraTreeNode>();
        if (!TryFindPath(root, target, path))
            return;

        // path includes root..target; expand every ancestor of the target.
        for (var i = 0; i < path.Count - 1; i++)
            path[i].IsExpanded = true;
    }

    private static bool TryFindPath(InfraTreeNode current, InfraTreeNode target, List<InfraTreeNode> path)
    {
        path.Add(current);
        if (ReferenceEquals(current, target))
            return true;

        foreach (var child in current.Children)
        {
            if (TryFindPath(child, target, path))
                return true;
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    private void RefreshDetailPanes()
    {
        NotifyDetailTabVisibility();
        var node = SelectedInfraNode ?? _pinnedInfraNode;
        RefreshSelectedVm();
        RefreshGeneralProperties(node);
        RefreshStorageProperties(node);
        RefreshNetworkProperties(node);
        RefreshConsoleProperties(node);
        RefreshSnapshotProperties();
        RefreshPerformanceProperties(node);
    }

    private void NotifyDetailTabVisibility()
    {
        OnPropertyChanged(nameof(IsStorageSelection));
        OnPropertyChanged(nameof(ShowDetailNetwork));
        OnPropertyChanged(nameof(ShowDetailConsole));
        OnPropertyChanged(nameof(ShowDetailSnapshots));
        OnPropertyChanged(nameof(ShowDetailPerformance));
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
        SnapshotTypeOptions.Clear();
        SnapshotStatusMessage = string.Empty;
        CanManageSnapshots = SelectedVm is { is_a_template: false, is_a_snapshot: false, is_control_domain: false };
        if (!CanManageSnapshots)
        {
            SelectedSnapshotType = null;
            HasSnapshotItems = false;
            OnPropertyChanged(nameof(CanTakeSnapshot));
            OnPropertyChanged(nameof(SnapshotTypeHint));
            TakeSnapshotCommand.NotifyCanExecuteChanged();
            return;
        }

        var vm = SelectedVm!;
        var operations = vm.allowed_operations ?? [];
        if (operations.Contains(XenAPI.vm_operations.snapshot))
        {
            SnapshotTypeOptions.Add(new SnapshotTypeOption(
                XenAdmin.Actions.SnapshotType.DISK,
                "Disk snapshot",
                "Captures virtual disks without guest memory."));
        }

        if (!Helpers.QuebecOrGreater(vm.Connection)
            && operations.Contains(XenAPI.vm_operations.snapshot_with_quiesce)
            && !Helpers.FeatureForbidden(vm, XenAPI.Host.RestrictVss))
        {
            SnapshotTypeOptions.Add(new SnapshotTypeOption(
                XenAdmin.Actions.SnapshotType.QUIESCED_DISK,
                "Quiesced disk snapshot",
                "Requests a guest-consistent disk snapshot through the installed management tools."));
        }

        if (operations.Contains(XenAPI.vm_operations.checkpoint)
            && !Helpers.FeatureForbidden(vm, XenAPI.Host.RestrictCheckpoint))
        {
            SnapshotTypeOptions.Add(new SnapshotTypeOption(
                XenAdmin.Actions.SnapshotType.DISK_AND_MEMORY,
                "Disk and memory checkpoint",
                "Captures virtual disks and running memory so the VM can return to the exact execution state."));
        }

        var previousType = SelectedSnapshotType?.Type;
        SelectedSnapshotType = SnapshotTypeOptions.FirstOrDefault(o => o.Type == previousType)
                               ?? SnapshotTypeOptions.FirstOrDefault();
        OnPropertyChanged(nameof(CanTakeSnapshot));
        OnPropertyChanged(nameof(SnapshotTypeHint));
        TakeSnapshotCommand.NotifyCanExecuteChanged();

        foreach (var row in SnapshotSummaryBuilder.Build(vm))
            SnapshotItems.Add(row);

        HasSnapshotItems = vm.snapshots is { Count: > 0 };
        if (SnapshotTypeOptions.Count == 0)
            SnapshotStatusMessage = "This VM does not currently allow a snapshot or checkpoint.";
        else if (!HasSnapshotItems)
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

        var key = ConsoleSessionSyncPolicy.SessionKey(
            live.Connection.Hostname ?? string.Empty,
            live.Console.opaque_ref ?? string.Empty,
            live.Console.location ?? string.Empty,
            live.DomainId);

        // Guest VM reboots: reconnect when the RFB target changes (new console / new
        // domid) or the transport drops, so the last pre-reboot frame is not left frozen.
        // Host control-domain: never retry the same key after drop — holding HTTP CONNECT
        // stalls host reboot/shutdown. Callers that want a fresh host session still clear
        // _activeConsoleKey first (selection change, power-op done).
        var sameTarget = string.Equals(_activeConsoleKey, key, StringComparison.Ordinal);
        if (!ConsoleSessionSyncPolicy.ShouldReplaceSession(
                _activeConsoleKey,
                key,
                _consoleSession.IsConnected,
                _consoleSession.IsConnecting,
                live.IsControlDomain))
        {
            return;
        }

        if (sameTarget && DateTime.UtcNow < _nextGuestConsoleRetryUtc)
            return;

        if (!sameTarget)
            CloseConsolePopOut();

        _activeConsoleKey = key;
        if (sameTarget)
            _nextGuestConsoleRetryUtc = DateTime.UtcNow.AddMilliseconds(1500);

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
        IsConsoleConnecting = _consoleSession.IsConnecting
                              || (!_consoleSession.IsConnected
                                  && !string.IsNullOrEmpty(_consoleSession.StatusMessage)
                                  && _consoleSession.StatusMessage.Contains("Connecting", StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasConsoleFrame));
        OnPropertyChanged(nameof(ShowEmbeddedConsole));
        if (!HasConsoleFrame)
            ConsoleInputHint = string.Empty;
        else if (string.IsNullOrEmpty(ConsoleInputHint))
            ConsoleInputHint = "Click the console to send keyboard and mouse to the guest.";

        QueueGuestConsoleRetryIfDropped();
    }

    /// <summary>
    /// Guest RFB often drops during reboot while power_state stays Running. Re-evaluate
    /// on the next UI turn so we reconnect without waiting for cache churn — and without
    /// recursing into Start() from the Stop() that Start itself performs.
    /// </summary>
    private void QueueGuestConsoleRetryIfDropped()
    {
        if (_consoleSession.IsConnected || _consoleSession.IsConnecting || _activeConsoleKey == null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_consoleSession.IsConnected || _consoleSession.IsConnecting || _activeConsoleKey == null)
                return;
            RefreshConsoleProperties(SelectedInfraNode ?? _pinnedInfraNode);
        });
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
