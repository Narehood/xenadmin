using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class RdpConnectViewModel : ObservableObject
{
    private readonly Action<RdpEndpoint> _connect;
    public RdpConnectViewModel(string guestName, IReadOnlyList<string> addresses, Action<RdpEndpoint> connect)
    {
        GuestName = guestName;
        Addresses = addresses;
        Address = addresses.FirstOrDefault() ?? string.Empty;
        SelectedAddress = addresses.FirstOrDefault();
        _connect = connect;
    }
    public string GuestName { get; }
    public IReadOnlyList<string> Addresses { get; }
    public bool HasAddresses => Addresses.Count != 0;
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private string? _selectedAddress;
    [ObservableProperty] private string _port = "3389";
    [ObservableProperty] private string _error = string.Empty;
    public bool HasError => !string.IsNullOrEmpty(Error);
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnAddressChanged(string value) => Error = string.Empty;
    partial void OnPortChanged(string value) => Error = string.Empty;
    partial void OnSelectedAddressChanged(string? value) { if (value != null) Address = value; }

    [RelayCommand]
    private void Connect()
    {
        if (!RdpEndpoint.TryCreate(Address, Port, out var endpoint))
        {
            Error = "Enter a guest IPv4 or IPv6 address and a port from 1 to 65535. Loopback, wildcard and multicast addresses are unavailable.";
            return;
        }
        _connect(endpoint!);
    }
}
