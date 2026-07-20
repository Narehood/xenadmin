using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.Controls;

/// <summary>
/// Fits an RFB framebuffer into the available space (Uniform), forwards pointer/keyboard,
/// and applies the remote cursor when the pointer is over the desktop.
/// </summary>
public sealed class RfbConsoleView : Control
{
    public static readonly StyledProperty<WriteableBitmap?> FrameProperty =
        AvaloniaProperty.Register<RfbConsoleView, WriteableBitmap?>(nameof(Frame));

    public static readonly StyledProperty<HostedConsoleSession?> SessionProperty =
        AvaloniaProperty.Register<RfbConsoleView, HostedConsoleSession?>(nameof(Session));

    public static readonly RoutedEvent<RoutedEventArgs> FocusCaptureChangedEvent =
        RoutedEvent.Register<RfbConsoleView, RoutedEventArgs>(
            nameof(FocusCaptureChanged),
            RoutingStrategies.Bubble);

    private int _buttonMask;
    private readonly Dictionary<Key, int> _pressed = new();
    private HostedConsoleSession? _subscribedSession;
    private Cursor? _remoteCursor;
    private bool _pointerOverDesktop;

    static RfbConsoleView()
    {
        AffectsRender<RfbConsoleView>(FrameProperty, SessionProperty);
        FocusableProperty.OverrideDefaultValue<RfbConsoleView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<RfbConsoleView>(true);
    }

    public event EventHandler<RoutedEventArgs> FocusCaptureChanged
    {
        add => AddHandler(FocusCaptureChangedEvent, value);
        remove => RemoveHandler(FocusCaptureChangedEvent, value);
    }

    public WriteableBitmap? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public HostedConsoleSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FrameProperty)
            InvalidateVisual();
        else if (change.Property == SessionProperty)
            SubscribeSession(change.GetNewValue<HostedConsoleSession?>());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SubscribeSession(null);
        ClearRemoteCursor();
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribeSession(HostedConsoleSession? session)
    {
        if (ReferenceEquals(_subscribedSession, session))
            return;
        if (_subscribedSession != null)
        {
            _subscribedSession.StateChanged -= OnSessionStateChanged;
            _subscribedSession.CursorChanged -= OnRemoteCursorChanged;
        }

        _subscribedSession = session;
        ClearRemoteCursor();

        if (_subscribedSession != null)
        {
            _subscribedSession.StateChanged += OnSessionStateChanged;
            _subscribedSession.CursorChanged += OnRemoteCursorChanged;
            RebuildRemoteCursor();
        }
    }

    private void OnSessionStateChanged() => InvalidateVisual();

    private void OnRemoteCursorChanged() => RebuildRemoteCursor();

    private void RebuildRemoteCursor()
    {
        ClearRemoteCursor();
        var session = Session;
        var bmp = session?.CursorBitmap;
        if (bmp == null || session == null)
        {
            UpdatePointerCursor();
            return;
        }

        try
        {
            _remoteCursor = new Cursor(bmp, session.CursorHotspot);
        }
        catch
        {
            _remoteCursor = null;
        }

        UpdatePointerCursor();
    }

    private void ClearRemoteCursor()
    {
        _remoteCursor = null;
    }

    private void UpdatePointerCursor()
    {
        Cursor = _pointerOverDesktop
            ? _remoteCursor ?? new Cursor(StandardCursorType.None)
            : Cursor.Default;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Tab content often measures with infinite height; never expand to the
        // native framebuffer size or the viewer will blow out sibling rows.
        var width = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
            ? 640
            : Math.Max(0, availableSize.Width);
        var height = double.IsInfinity(availableSize.Height) || double.IsNaN(availableSize.Height)
            ? 360
            : Math.Max(0, availableSize.Height);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        InvalidateVisual();
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x12)), bounds);

        var frame = Frame;
        if (frame == null || !TryGetDisplayRect(out var dest, out _))
            return;

        context.DrawImage(frame, new Rect(frame.Size), dest);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Pointer.Capture(this);
        UpdateButtons(e, pressed: true);
        SendPointer(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsFocused)
            return;
        UpdateButtons(e, pressed: false);
        SendPointer(e.GetPosition(this));
        if (_buttonMask == 0)
            e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsFocused)
            return;

        var pos = e.GetPosition(this);
        var over = TryMapToFramebuffer(pos, out _, out _);
        if (over != _pointerOverDesktop)
        {
            _pointerOverDesktop = over;
            UpdatePointerCursor();
        }

        if (Session?.IsConnected == true)
        {
            SendPointer(pos);
            e.Handled = true;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerOverDesktop = false;
        UpdatePointerCursor();
        if (!IsFocused)
            return;
        _buttonMask = 0;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Let page ScrollViewer keep scrolling until the console is clicked/focused.
        if (!IsFocused)
            return;

        if (!TryMapToFramebuffer(e.GetPosition(this), out var x, out var y))
            return;

        var steps = (int)Math.Round(-e.Delta.Y);
        if (steps == 0)
            steps = e.Delta.Y < 0 ? 1 : -1;
        Session?.SendPointerWheel(x, y, steps);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsFocused)
            return;
        if (SendKey(e, down: true))
            e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!IsFocused)
            return;
        if (SendKey(e, down: false))
            e.Handled = true;
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        RaiseEvent(new RoutedEventArgs(FocusCaptureChangedEvent, this));
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        foreach (var sym in _pressed.Values)
            Session?.SendKey(false, sym);
        _pressed.Clear();
        if (_buttonMask != 0)
        {
            _buttonMask = 0;
            Session?.SendPointer(0, 0, 0);
        }

        RaiseEvent(new RoutedEventArgs(FocusCaptureChangedEvent, this));
    }

    private bool SendKey(KeyEventArgs e, bool down)
    {
        if (Session?.IsConnected != true)
            return false;

        int sym;
        if (down)
        {
            sym = RfbKeySym.FromKeyEvent(e);
            if (sym <= 0)
                return false;
            _pressed[e.Key] = sym;
        }
        else
        {
            if (!_pressed.Remove(e.Key, out sym))
            {
                sym = RfbKeySym.FromKeyEvent(e);
                if (sym <= 0)
                    return false;
            }
        }

        Session.SendKey(down, sym);
        return true;
    }

    private void UpdateButtons(PointerEventArgs e, bool pressed)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (pressed)
        {
            if (props.IsLeftButtonPressed)
                _buttonMask |= 1;
            if (props.IsMiddleButtonPressed)
                _buttonMask |= 2;
            if (props.IsRightButtonPressed)
                _buttonMask |= 4;
        }
        else
        {
            if (!props.IsLeftButtonPressed)
                _buttonMask &= ~1;
            if (!props.IsMiddleButtonPressed)
                _buttonMask &= ~2;
            if (!props.IsRightButtonPressed)
                _buttonMask &= ~4;
        }
    }

    private void SendPointer(Point local)
    {
        if (!TryMapToFramebuffer(local, out var x, out var y))
            return;
        Session?.SendPointer(_buttonMask, x, y);
    }

    private bool TryMapToFramebuffer(Point local, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (!TryGetDisplayRect(out var dest, out var desk))
            return false;
        if (!dest.Contains(local))
            return false;

        var scaleX = desk.Width / dest.Width;
        var scaleY = desk.Height / dest.Height;
        x = (int)((local.X - dest.X) * scaleX);
        y = (int)((local.Y - dest.Y) * scaleY);
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (x >= desk.Width) x = desk.Width - 1;
        if (y >= desk.Height) y = desk.Height - 1;
        return true;
    }

    private bool TryGetDisplayRect(out Rect dest, out PixelSize desk)
    {
        dest = default;
        desk = default;
        var frame = Frame;
        var session = Session;
        var dw = session?.DesktopWidth ?? frame?.PixelSize.Width ?? 0;
        var dh = session?.DesktopHeight ?? frame?.PixelSize.Height ?? 0;
        if (frame == null || dw <= 0 || dh <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
            return false;

        desk = new PixelSize(dw, dh);
        var scale = Math.Min(Bounds.Width / dw, Bounds.Height / dh);
        var w = dw * scale;
        var h = dh * scale;
        var x = (Bounds.Width - w) / 2;
        var y = (Bounds.Height - h) / 2;
        dest = new Rect(x, y, w, h);
        return true;
    }
}
