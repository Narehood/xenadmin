using CommunityToolkit.Mvvm.ComponentModel;

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
    private bool _isPublicIp;
}
