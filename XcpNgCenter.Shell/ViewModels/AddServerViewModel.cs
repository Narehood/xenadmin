using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace XcpNgCenter.Shell.ViewModels;

public partial class AddServerViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly Action _close;

    public AddServerViewModel(MainViewModel main, Action close)
    {
        _main = main;
        _close = close;
        Username = string.IsNullOrWhiteSpace(main.Username) ? "root" : main.Username;
    }

    public bool CanPersistPasswords => _main.CanPersistPasswords;
    public string RememberPasswordLabel => _main.RememberPasswordLabel;

    [ObservableProperty]
    private string _hostInput = string.Empty;

    [ObservableProperty]
    private bool _showPublicIpWarning;

    [ObservableProperty]
    private bool _acknowledgePublicIp;

    [ObservableProperty]
    private string _username = "root";

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _rememberPassword;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    partial void OnHostInputChanged(string value)
    {
        ShowPublicIpWarning = _main.ShouldWarnForPublicIp(value);
        AcknowledgePublicIp = false;
    }

    [RelayCommand]
    private void Connect()
    {
        var error = _main.TryBeginConnect(
            HostInput,
            Username,
            Password,
            RememberPassword,
            publicIpAcknowledged: AcknowledgePublicIp);
        if (error != null)
        {
            StatusMessage = error;
            return;
        }

        _close();
    }
}
