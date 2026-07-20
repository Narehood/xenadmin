using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Rfb;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Hosted RFB console session for the Avalonia shell.
/// Ports the connect path from WinForms <c>XSVNCScreen.ConnectHostedConsole</c>.
/// </summary>
public sealed class HostedConsoleSession : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private AvaloniaRfbFramebuffer? _framebuffer;
    private RfbClient? _client;
    private Stream? _stream;
    private Session? _session;
    private int _generation;
    private bool _disposed;

    public WriteableBitmap? Bitmap => _framebuffer?.Bitmap;

    public WriteableBitmap? CursorBitmap => _framebuffer?.CursorBitmap;

    public PixelPoint CursorHotspot => _framebuffer?.CursorHotspot ?? default;

    public int DesktopWidth => _framebuffer?.DesktopWidth ?? 0;

    public int DesktopHeight => _framebuffer?.DesktopHeight ?? 0;

    public string StatusMessage { get; private set; } = string.Empty;

    public bool IsConnected { get; private set; }

    public bool HasFrame => Bitmap != null;

    public event Action? StateChanged;
    public event Action? CursorChanged;

    public void Start(LiveRfbTarget target)
    {
        Stop();

        var cts = new CancellationTokenSource();
        AvaloniaRfbFramebuffer framebuffer;
        int generation;

        lock (_gate)
        {
            if (_disposed)
                return;

            _cts = cts;
            generation = ++_generation;
            framebuffer = new AvaloniaRfbFramebuffer(target.VmName, target.Uuid);
            framebuffer.FramePresented += OnFramePresented;
            framebuffer.DesktopResized += OnDesktopResized;
            framebuffer.CursorChanged += OnCursorChanged;
            _framebuffer = framebuffer;
            IsConnected = false;
            StatusMessage = "Connecting to RFB console…";
        }

        RaiseStateChanged();

        _ = System.Threading.Tasks.Task.Run(() => ConnectWorker(target, framebuffer, generation, cts.Token), cts.Token);
    }

    private void ConnectWorker(LiveRfbTarget target, AvaloniaRfbFramebuffer framebuffer, int generation, CancellationToken token)
    {
        Stream? stream = null;
        Session? session = null;
        RfbClient? client = null;

        try
        {
            token.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(target.Console.location))
                throw new InvalidOperationException("Console location is empty.");

            session = target.Connection.DuplicateSession();
            token.ThrowIfCancellationRequested();

            var uri = new Uri(target.Console.location);
            stream = HTTPHelper.CONNECT(uri, target.Connection, session.opaque_ref, false);
            token.ThrowIfCancellationRequested();

            client = new RfbClient(framebuffer, stream, startPaused: false);
            client.ErrorOccurred += (_, ex) =>
            {
                SetStatus($"Console error: {ex.Message}", connected: false);
            };
            client.ConnectionSuccess += (_, _) =>
            {
                SetStatus("Live console — click to focus for keyboard/mouse.", connected: true);
            };

            lock (_gate)
            {
                if (_disposed || _generation != generation || token.IsCancellationRequested)
                {
                    client.Close();
                    stream.Dispose();
                    return;
                }

                _session = session;
                _stream = stream;
                _client = client;
            }

            // Hosted XAPI consoles typically use auth scheme 1 (none).
            client.Connect(Array.Empty<char>());
        }
        catch (OperationCanceledException)
        {
            stream?.Dispose();
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            if (!_disposed && _generation == generation)
                SetStatus($"Console connect failed: {ex.Message}", connected: false);
            Debug.WriteLine(ex);
        }
    }

    private void OnFramePresented()
    {
        if (_disposed)
            return;
        RaiseStateChanged();
    }

    private void OnCursorChanged()
    {
        if (_disposed)
            return;
        if (Dispatcher.UIThread.CheckAccess())
            CursorChanged?.Invoke();
        else
            Dispatcher.UIThread.Post(() => CursorChanged?.Invoke());
    }

    private void SetStatus(string message, bool connected)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            StatusMessage = message;
            IsConnected = connected;
        }

        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            StateChanged?.Invoke();
        else
            Dispatcher.UIThread.Post(() => StateChanged?.Invoke());
    }

    public void SendPointer(int buttonMask, int x, int y)
    {
        RfbClient? client;
        lock (_gate)
            client = IsConnected ? _client : null;
        try { client?.PointerEvent(buttonMask, x, y); }
        catch { /* connection may have dropped */ }
    }

    public void SendPointerWheel(int x, int y, int steps)
    {
        RfbClient? client;
        lock (_gate)
            client = IsConnected ? _client : null;
        try { client?.PointerWheelEvent(x, y, steps); }
        catch { /* connection may have dropped */ }
    }

    public void SendKey(bool down, int keysym)
    {
        if (keysym <= 0)
            return;
        RfbClient? client;
        lock (_gate)
            client = IsConnected ? _client : null;
        try { client?.keyCodeEvent(down, keysym); }
        catch { /* connection may have dropped */ }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        RfbClient? client;
        Stream? stream;
        AvaloniaRfbFramebuffer? framebuffer;

        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            client = _client;
            _client = null;
            stream = _stream;
            _stream = null;
            _session = null;
            framebuffer = _framebuffer;
            _framebuffer = null;
            IsConnected = false;
            if (!string.IsNullOrEmpty(StatusMessage) && StatusMessage.StartsWith("Live console", StringComparison.Ordinal))
                StatusMessage = "Console disconnected.";
        }

        try { cts?.Cancel(); } catch { /* ignore */ }
        try { client?.Close(); } catch { /* ignore */ }
        try { stream?.Dispose(); } catch { /* ignore */ }
        if (framebuffer != null)
        {
            framebuffer.FramePresented -= OnFramePresented;
            framebuffer.DesktopResized -= OnDesktopResized;
            framebuffer.CursorChanged -= OnCursorChanged;
            try { framebuffer.Dispose(); } catch { /* ignore */ }
        }
        try { cts?.Dispose(); } catch { /* ignore */ }

        RaiseStateChanged();
    }

    private void OnDesktopResized(int _, int __) => OnFramePresented();

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        Stop();
    }
}

/// <summary>Resolved hosted RFB console for a selected infra node.</summary>
public readonly record struct LiveRfbTarget(
    IXenConnection Connection,
    XenAPI.Console Console,
    string ObjectLabel,
    string VmName,
    string Uuid);
