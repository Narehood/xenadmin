using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.Controls;

/// <summary>
/// Fits an RFB framebuffer into the available space (Uniform) and forwards pointer/keyboard input.
/// </summary>
public sealed class RfbConsoleView : Control
{
    public static readonly StyledProperty<WriteableBitmap?> FrameProperty =
        AvaloniaProperty.Register<RfbConsoleView, WriteableBitmap?>(nameof(Frame));

    public static readonly StyledProperty<HostedConsoleSession?> SessionProperty =
        AvaloniaProperty.Register<RfbConsoleView, HostedConsoleSession?>(nameof(Session));

    private int _buttonMask;
    private readonly HashSet<Key> _pressed = new();
    private HostedConsoleSession? _subscribedSession;

    static RfbConsoleView()
    {
        AffectsRender<RfbConsoleView>(FrameProperty, SessionProperty);
        FocusableProperty.OverrideDefaultValue<RfbConsoleView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<RfbConsoleView>(true);
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
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribeSession(HostedConsoleSession? session)
    {
        if (ReferenceEquals(_subscribedSession, session))
            return;
        if (_subscribedSession != null)
            _subscribedSession.StateChanged -= OnSessionStateChanged;
        _subscribedSession = session;
        if (_subscribedSession != null)
            _subscribedSession.StateChanged += OnSessionStateChanged;
    }

    private void OnSessionStateChanged() => InvalidateVisual();

    protected override Size ArrangeOverride(Size finalSize)
    {
        InvalidateVisual();
        return base.ArrangeOverride(finalSize);
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
        UpdateButtons(e, pressed: false);
        SendPointer(e.GetPosition(this));
        if (_buttonMask == 0)
            e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Session?.IsConnected == true)
        {
            SendPointer(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!TryMapToFramebuffer(e.GetPosition(this), out var x, out var y))
            return;

        // Avalonia delta is typically ±1 per notch; RFB wants signed step count.
        var steps = (int)Math.Round(-e.Delta.Y);
        if (steps == 0)
            steps = e.Delta.Y < 0 ? 1 : -1;
        Session?.SendPointerWheel(x, y, steps);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (SendKey(e, down: true))
            e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (SendKey(e, down: false))
            e.Handled = true;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        // Release any held keys/buttons so the guest does not stick.
        foreach (var key in _pressed.ToArray())
        {
            var sym = RfbKeySym.FromKey(key, KeyModifiers.None);
            if (sym > 0)
                Session?.SendKey(false, sym);
        }
        _pressed.Clear();
        if (_buttonMask != 0)
        {
            _buttonMask = 0;
            Session?.SendPointer(0, 0, 0);
        }
    }

    private bool SendKey(KeyEventArgs e, bool down)
    {
        if (Session?.IsConnected != true)
            return false;

        var sym = RfbKeySym.FromKey(e.Key, e.KeyModifiers);
        if (sym <= 0)
            return false;

        if (down)
            _pressed.Add(e.Key);
        else
            _pressed.Remove(e.Key);

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
            // On release, clear buttons that are no longer down.
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
