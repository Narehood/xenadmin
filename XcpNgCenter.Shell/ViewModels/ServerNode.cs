using CommunityToolkit.Mvvm.ComponentModel;
using XenAdmin.Network;

namespace XcpNgCenter.Shell.ViewModels;

public partial class ServerNode : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _address = string.Empty;

    [ObservableProperty]
    private string _status = "Saved";

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isPublicIp;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _rememberPassword;

    /// <summary>Last username used for this server (persisted; password is not).</summary>
    public string Username { get; set; } = "root";

    /// <summary>
    /// In-memory password for reconnect after a failed/disconnected session.
    /// Never written to disk except via <see cref="Services.SavedServerStore"/> when RememberPassword is set.
    /// </summary>
    public string? Password { get; set; }

    public int Port { get; set; }

    public string Hostname { get; set; } = string.Empty;

    public IXenConnection? Connection { get; set; }

    public bool IsDisconnected => !IsConnected && !IsConnecting;
}
