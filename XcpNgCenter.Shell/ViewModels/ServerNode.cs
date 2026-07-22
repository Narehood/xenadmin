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

    /// <summary>Last username used for this server (persisted; password is not).</summary>
    public string Username { get; set; } = "root";

    public IXenConnection? Connection { get; set; }
}
