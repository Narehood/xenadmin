using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
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
    public string DestinationAuthority => RdpEndpoint.TryCreate(Address, Port, out var endpoint)
        ? endpoint!.Authority : string.Empty;
    public bool HasDestination => !string.IsNullOrEmpty(DestinationAuthority);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationAuthority))]
    [NotifyPropertyChangedFor(nameof(HasDestination))]
    private string _address = string.Empty;
    [ObservableProperty] private string? _selectedAddress;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationAuthority))]
    [NotifyPropertyChangedFor(nameof(HasDestination))]
    private string _port = "3389";
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReviewMessage))]
    private string _reviewMessage = string.Empty;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasReviewMessage => !string.IsNullOrEmpty(ReviewMessage);
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnAddressChanged(string value) => ClearMessages();
    partial void OnPortChanged(string value) => ClearMessages();
    partial void OnSelectedAddressChanged(string? value) { if (value != null) Address = value; }

    private void ClearMessages()
    {
        Error = string.Empty;
        ReviewMessage = string.Empty;
    }

    [RelayCommand]
    private void Connect()
    {
        if (!RdpEndpoint.TryCreate(Address, Port, out var endpoint))
        {
            Error = "Enter a guest IPv4 or IPv6 address and a port from 1 to 65535. Loopback, wildcard and multicast addresses are unavailable.";
            return;
        }
        var address = endpoint!.Address.ToString();
        var port = endpoint.Port.ToString(CultureInfo.InvariantCulture);
        if (Address != address || Port != port)
        {
            Address = address;
            Port = port;
            ReviewMessage = "Review the destination below, then choose Open Remote Desktop again.";
            return;
        }
        ReviewMessage = string.Empty;
        _connect(endpoint!);
    }
}
