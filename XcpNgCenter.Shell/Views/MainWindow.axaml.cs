using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XcpNgCenter.Shell.Controls;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _boundVm;
    private double _savedInfraScrollOffset;
    private bool _hasSavedInfraScroll;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnConsoleShortcutKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnConsoleShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.HasConsoleFrame)
            return;

        if (ConsoleShortcutMatcher.Matches(e, vm.ConsoleFullscreenShortcut))
        {
            vm.ToggleConsoleFullScreenCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ConsoleShortcutMatcher.Matches(e, vm.ConsoleDockShortcut))
        {
            vm.ToggleConsoleDockCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm != null)
        {
            _boundVm.TreeLayoutChanging -= OnTreeLayoutChanging;
            _boundVm.TreeLayoutChanged -= OnTreeLayoutChanged;
        }

        _boundVm = DataContext as MainViewModel;
        if (_boundVm == null)
            return;

        _boundVm.TreeLayoutChanging += OnTreeLayoutChanging;
        _boundVm.TreeLayoutChanged += OnTreeLayoutChanged;
    }

    private void OnTreeLayoutChanging()
    {
        var scroll = FindInfraScrollViewer();
        if (scroll == null)
            return;
        _savedInfraScrollOffset = scroll.Offset.Y;
        _hasSavedInfraScroll = true;
    }

    private void OnTreeLayoutChanged()
    {
        if (!_hasSavedInfraScroll)
            return;

        var offset = _savedInfraScrollOffset;
        _hasSavedInfraScroll = false;
        Dispatcher.UIThread.Post(() =>
        {
            var scroll = FindInfraScrollViewer();
            if (scroll == null)
                return;
            scroll.Offset = scroll.Offset.WithY(offset);
        }, DispatcherPriority.Loaded);
    }

    private ScrollViewer? FindInfraScrollViewer()
    {
        if (InfraTree == null)
            return null;
        return InfraTree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    }

    private void OnConsoleFoldScrollSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyConsoleFoldHeight(e.NewSize.Height);
    }

    private void OnConsoleFoldScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // A scrollbar can shrink the viewport without changing the scroll viewer's outer size.
        if (Math.Abs(e.ViewportDelta.Y) <= 0.5)
            return;
        ApplyConsoleFoldHeight(ConsoleFoldScroll.Bounds.Height);
    }

    private void ApplyConsoleFoldHeight(double arrangedHeight)
    {
        var height = ConsoleFoldLayout.SelectFoldHeight(ConsoleFoldScroll.Viewport.Height, arrangedHeight);
        if (height is not double next)
            return;
        if (!double.IsNaN(ConsoleFold.Height) && Math.Abs(ConsoleFold.Height - next) <= 0.5)
            return;
        ConsoleFold.Height = next;
    }

    private void OnConsoleFocusCaptureChanged(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not RfbConsoleView view)
            return;

        vm.SetConsoleInputFocused(view.IsFocused);
    }

    private void OnInfraTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        if (DataContext is not MainViewModel vm)
            return;

        var source = e.Source as Control;
        while (source != null && source is not TreeViewItem)
            source = source.GetVisualParent() as Control;

        if (source is TreeViewItem { DataContext: InfraTreeNode node })
            vm.SelectedInfraNode = node;
    }
}
