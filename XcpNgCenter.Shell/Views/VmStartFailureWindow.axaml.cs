using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace XcpNgCenter.Shell.Views;

public sealed class HostBootReasonRow
{
    public HostBootReasonRow(string hostName, string reason, bool canBoot)
    {
        HostName = hostName;
        Reason = reason;
        CanBoot = canBoot;
    }

    public string HostName { get; }
    public string Reason { get; }
    public bool CanBoot { get; }
    public string StatusLabel => CanBoot ? "OK" : "Blocked";
}

public partial class VmStartFailureWindow : Window
{
    public VmStartFailureWindow()
    {
        InitializeComponent();
    }

    public VmStartFailureWindow(string title, string summary, IReadOnlyList<HostBootReasonRow> rows) : this()
    {
        Title = title;
        DataContext = new VmStartFailureView(title, summary, rows);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}

public sealed class VmStartFailureView
{
    public VmStartFailureView(string title, string summary, IReadOnlyList<HostBootReasonRow> rows)
    {
        Title = title;
        Summary = summary;
        foreach (var row in rows)
            Rows.Add(row);
    }

    public string Title { get; }
    public string Summary { get; }
    public ObservableCollection<HostBootReasonRow> Rows { get; } = new();
}
