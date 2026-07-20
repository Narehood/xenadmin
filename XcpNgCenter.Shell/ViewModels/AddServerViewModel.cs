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
    private string _username = "root";

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _rememberPassword;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Connect()
    {
        var error = _main.TryBeginConnect(HostInput, Username, Password, RememberPassword);
        if (error != null)
        {
            StatusMessage = error;
            return;
        }

        _close();
    }
}
