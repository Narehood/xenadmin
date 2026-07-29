using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions;
using XenAdmin.Core;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class HostPropertiesViewModel : ViewModelBase
{
    private readonly Host _host;
    private readonly Action _close;
    private readonly Action<string>? _status;
    private readonly bool _initialAutostart;

    public HostPropertiesViewModel(Host host, Action close, Action<string>? status = null)
    {
        _host = host;
        _close = close;
        _status = status;
        NameLabel = host.name_label ?? string.Empty;
        Description = host.name_description ?? string.Empty;
        AutostartVms = host.GetVmAutostartEnabled();
        _initialAutostart = AutostartVms;
        Address = string.IsNullOrWhiteSpace(host.address) ? host.hostname ?? string.Empty : host.address;
        Role = Helpers.HostIsCoordinator(host) ? "Coordinator" : "Member";
        Product = FormatProduct(host);
        Uuid = host.uuid ?? string.Empty;
    }

    public string Address { get; }
    public string Role { get; }
    public string Product { get; }
    public string Uuid { get; }

    [ObservableProperty]
    private string _nameLabel = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _autostartVms;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Apply()
    {
        if (string.IsNullOrWhiteSpace(NameLabel))
        {
            StatusMessage = "Enter a host name.";
            return;
        }

        var name = NameLabel.Trim();
        var description = Description ?? string.Empty;
        var actions = new List<AsyncAction>();

        if (!string.Equals(name, _host.name_label, StringComparison.Ordinal))
        {
            actions.Add(new DelegatedAsyncAction(
                _host.Connection,
                $"Rename {_host.name_label}",
                "Renaming…",
                "Renamed.",
                session => Host.set_name_label(session, _host.opaque_ref, name),
                true,
                "host.set_name_label"));
        }

        if (!string.Equals(description, _host.name_description ?? string.Empty, StringComparison.Ordinal))
        {
            actions.Add(new DelegatedAsyncAction(
                _host.Connection,
                $"Update description for {name}",
                "Updating description…",
                "Description updated.",
                session => Host.set_name_description(session, _host.opaque_ref, description),
                true,
                "host.set_name_description"));
        }

        if (AutostartVms != _initialAutostart)
            actions.Add(new ChangeHostAutostartAction(_host, AutostartVms));

        if (actions.Count == 0)
        {
            StatusMessage = "No changes to apply.";
            return;
        }

        foreach (var action in actions)
        {
            ShellActionRunner.Run(action, msg =>
            {
                StatusMessage = msg;
                _status?.Invoke(msg);
            });
        }

        _close();
    }

    [RelayCommand]
    private void Cancel() => _close();

    private static string FormatProduct(Host host)
    {
        var product = host.ProductVersionText();
        var brand = host.ProductBrand();
        if (string.IsNullOrWhiteSpace(product))
            return brand ?? "—";
        if (string.IsNullOrWhiteSpace(brand))
            return product;
        return $"{brand} {product}";
    }
}
