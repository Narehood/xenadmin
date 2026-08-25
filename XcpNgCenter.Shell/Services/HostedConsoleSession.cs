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

    public bool IsConnecting { get; private set; }

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
            IsConnecting = true;
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

            // DuplicateSession copies the main session opaque_ref onto a new TCP client.
            // Never Session.logout() it — that would destroy the pool connection.
            session = target.Connection.DuplicateSession();
            token.ThrowIfCancellationRequested();

            var uri = new Uri(target.Console.location);
            stream = HTTPHelper.CONNECT(uri, target.Connection, session.opaque_ref, false);
            token.ThrowIfCancellationRequested();

            client = new RfbClient(framebuffer, stream, startPaused: false);
            client.ErrorOccurred += (_, ex) =>
            {
                // Drop the HTTP CONNECT immediately. Holding an open dom0 console
                // proxy against a rebooting host can stall orderly shutdown.
                SetStatus($"Console error: {ex.Message}", connected: false);
                TearDownTransport(generation);
            };
            client.ConnectionSuccess += (_, _) =>
            {
                SetStatus("Live console — click to focus for keyboard/mouse.", connected: true);
            };

            RfbClient connectClient;
            lock (_gate)
            {
                if (_disposed || _generation != generation || token.IsCancellationRequested)
                {
                    client.Close();
                    SafeDispose(stream);
                    // Drop local refs only — do not logout DuplicateSession.
                    return;
                }

                _session = session;
                _stream = stream;
                _client = client;
                connectClient = client;
                session = null;
                stream = null;
                client = null;
            }

            // Hosted XAPI consoles typically use auth scheme 1 (none).
            connectClient.Connect(Array.Empty<char>());
        }
        catch (OperationCanceledException)
        {
            SafeDispose(client);
            SafeDispose(stream);
        }
        catch (Exception ex)
        {
            SafeDispose(client);
            SafeDispose(stream);
            if (!_disposed && _generation == generation)
                SetStatus($"Console connect failed: {ex.Message}", connected: false);
            Debug.WriteLine(ex);
        }
    }

    /// <summary>
    /// Closes RFB/HTTP transport for a generation without wiping the framebuffer
    /// status the UI is already showing (used from ErrorOccurred).
    /// </summary>
    private void TearDownTransport(int generation)
    {
        RfbClient? client;
        Stream? stream;

        lock (_gate)
        {
            if (_disposed || _generation != generation)
                return;

            client = _client;
            _client = null;
            stream = _stream;
            _stream = null;
            _session = null;
            IsConnected = false;
            IsConnecting = false;
        }

        try { client?.Close(); } catch { /* ignore */ }
        SafeDispose(stream);
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
            IsConnecting = false;
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
            // DuplicateSession shares the pool opaque_ref — never logout here.
            _session = null;
            framebuffer = _framebuffer;
            _framebuffer = null;
            IsConnected = false;
            IsConnecting = false;
            if (!string.IsNullOrEmpty(StatusMessage) && StatusMessage.StartsWith("Live console", StringComparison.Ordinal))
                StatusMessage = "Console disconnected.";
        }

        try { cts?.Cancel(); } catch { /* ignore */ }
        try { client?.Close(); } catch { /* ignore */ }
        SafeDispose(stream);
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

    private static void SafeDispose(IDisposable? d)
    {
        try { d?.Dispose(); } catch { /* ignore */ }
    }

    private static void SafeDispose(RfbClient? client)
    {
        try { client?.Close(); } catch { /* ignore */ }
    }
}

/// <summary>Resolved hosted RFB console for a selected infra node.</summary>
public readonly record struct LiveRfbTarget(
    IXenConnection Connection,
    XenAPI.Console Console,
    string ObjectLabel,
    string VmName,
    string Uuid,
    bool IsControlDomain,
    long DomainId);
