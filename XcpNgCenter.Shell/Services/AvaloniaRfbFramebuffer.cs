using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using XcpNgCenter.Rfb;

namespace XcpNgCenter.Shell.Services;

/// <summary>
/// Read-only Avalonia framebuffer backed by a BGRA32 pixel store and <see cref="WriteableBitmap"/>.
/// RFB helper-thread callbacks update the store; UI-thread sync refreshes the bitmap.
/// </summary>
public sealed class AvaloniaRfbFramebuffer : IRfbFramebuffer, IDisposable
{
    private readonly object _gate = new();
    private byte[] _pixels = Array.Empty<byte>();
    private int _width;
    private int _height;
    private bool _disposed;
    private bool _uiUpdateQueued;
    private bool _cursorUpdateQueued;
    private WriteableBitmap? _bitmap;
    private WriteableBitmap? _cursorBitmap;
    private int _cursorHotspotX;
    private int _cursorHotspotY;
    private byte[]? _pendingCursorBgra;
    private int _pendingCursorStride;
    private int _pendingCursorWidth;
    private int _pendingCursorHeight;
    private int _pendingCursorHotX;
    private int _pendingCursorHotY;

    public AvaloniaRfbFramebuffer(string vmName, string uuid)
    {
        VmName = vmName;
        Uuid = uuid;
    }

    public string VmName { get; }
    public string Uuid { get; }

    public WriteableBitmap? Bitmap
    {
        get
        {
            lock (_gate)
                return _bitmap;
        }
    }

    public int DesktopWidth
    {
        get
        {
            lock (_gate)
                return _width;
        }
    }

    public int DesktopHeight
    {
        get
        {
            lock (_gate)
                return _height;
        }
    }

    public WriteableBitmap? CursorBitmap
    {
        get
        {
            lock (_gate)
                return _cursorBitmap;
        }
    }

    public PixelPoint CursorHotspot
    {
        get
        {
            lock (_gate)
                return new PixelPoint(_cursorHotspotX, _cursorHotspotY);
        }
    }

    public event Action? FramePresented;
    public event Action<int, int>? DesktopResized;
    public event Action? CursorChanged;

    public void Bell()
    {
        // Preview: no audio.
    }

    public void CutText(string text)
    {
        // Guest clipboard sync is deferred.
    }

    public void SetCursor(byte[] bgra, int offset, int stride, int hotspotX, int hotspotY, int width, int height)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            if (width <= 0 || height <= 0 || bgra.Length == 0)
            {
                _pendingCursorBgra = null;
                _pendingCursorWidth = 0;
                _pendingCursorHeight = 0;
            }
            else
            {
                var copy = new byte[height * width * 4];
                for (var row = 0; row < height; row++)
                {
                    Buffer.BlockCopy(
                        bgra,
                        offset + row * stride,
                        copy,
                        row * width * 4,
                        width * 4);
                }

                _pendingCursorBgra = copy;
                _pendingCursorStride = width * 4;
                _pendingCursorWidth = width;
                _pendingCursorHeight = height;
                _pendingCursorHotX = Math.Clamp(hotspotX, 0, Math.Max(0, width - 1));
                _pendingCursorHotY = Math.Clamp(hotspotY, 0, Math.Max(0, height - 1));
            }

            if (_cursorUpdateQueued)
                return;
            _cursorUpdateQueued = true;
        }

        Dispatcher.UIThread.Post(ApplyPendingCursor);
    }

    private void ApplyPendingCursor()
    {
        WriteableBitmap? old = null;
        lock (_gate)
        {
            _cursorUpdateQueued = false;
            if (_disposed)
                return;

            old = _cursorBitmap;
            _cursorBitmap = null;

            if (_pendingCursorBgra != null && _pendingCursorWidth > 0 && _pendingCursorHeight > 0)
            {
                var bmp = new WriteableBitmap(
                    new PixelSize(_pendingCursorWidth, _pendingCursorHeight),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul);

                using (var fb = bmp.Lock())
                {
                    var srcStride = _pendingCursorStride;
                    var dstStride = fb.RowBytes;
                    for (var row = 0; row < _pendingCursorHeight; row++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            _pendingCursorBgra,
                            row * srcStride,
                            IntPtr.Add(fb.Address, row * dstStride),
                            Math.Min(srcStride, dstStride));
                    }
                }

                _cursorBitmap = bmp;
                _cursorHotspotX = _pendingCursorHotX;
                _cursorHotspotY = _pendingCursorHotY;
            }
            else
            {
                _cursorHotspotX = 0;
                _cursorHotspotY = 0;
            }
        }

        try { old?.Dispose(); } catch { /* ignore */ }
        CursorChanged?.Invoke();
    }

    public void DesktopSize(int width, int height)
    {
        if (width <= 0)
            width = 1;
        if (height <= 0)
            height = 1;

        lock (_gate)
        {
            if (_disposed)
                return;

            if (_width == width && _height == height && _pixels.Length == width * height * 4)
                return;

            _width = width;
            _height = height;
            _pixels = new byte[width * height * 4];
            _bitmap = null;
        }

        QueueUiSync(resized: true);
    }

    public void DrawImage(byte[] bgra, int offset, int stride, int x, int y, int width, int height)
    {
        lock (_gate)
        {
            if (_disposed || _width <= 0 || _height <= 0 || width <= 0 || height <= 0)
                return;

            var dstStride = _width * 4;
            for (var row = 0; row < height; row++)
            {
                var dy = y + row;
                if (dy < 0 || dy >= _height)
                    continue;

                var copyWidth = width;
                var srcCol = 0;
                var dstCol = x;
                if (dstCol < 0)
                {
                    srcCol = -dstCol;
                    copyWidth += dstCol;
                    dstCol = 0;
                }

                if (dstCol + copyWidth > _width)
                    copyWidth = _width - dstCol;
                if (copyWidth <= 0)
                    continue;

                Buffer.BlockCopy(
                    bgra,
                    offset + row * stride + srcCol * 4,
                    _pixels,
                    dy * dstStride + dstCol * 4,
                    copyWidth * 4);
            }
        }
    }

    public void FillRectangle(int x, int y, int width, int height, RfbColor color)
    {
        lock (_gate)
        {
            if (_disposed || _width <= 0 || _height <= 0 || width <= 0 || height <= 0)
                return;

            var b = color.B;
            var g = color.G;
            var r = color.R;
            var dstStride = _width * 4;

            for (var row = 0; row < height; row++)
            {
                var dy = y + row;
                if (dy < 0 || dy >= _height)
                    continue;

                for (var col = 0; col < width; col++)
                {
                    var dx = x + col;
                    if (dx < 0 || dx >= _width)
                        continue;
                    var i = dy * dstStride + dx * 4;
                    _pixels[i] = b;
                    _pixels[i + 1] = g;
                    _pixels[i + 2] = r;
                    _pixels[i + 3] = 0xFF;
                }
            }
        }
    }

    public void CopyRectangle(int x, int y, int width, int height, int dx, int dy)
    {
        lock (_gate)
        {
            if (_disposed || _width <= 0 || _height <= 0 || width <= 0 || height <= 0)
                return;

            var src = new byte[width * height * 4];
            var srcStride = width * 4;
            var dstStride = _width * 4;

            for (var row = 0; row < height; row++)
            {
                var sy = y + row;
                if (sy < 0 || sy >= _height)
                    continue;
                for (var col = 0; col < width; col++)
                {
                    var sx = x + col;
                    if (sx < 0 || sx >= _width)
                        continue;
                    Buffer.BlockCopy(_pixels, sy * dstStride + sx * 4, src, row * srcStride + col * 4, 4);
                }
            }

            for (var row = 0; row < height; row++)
            {
                var ty = dy + row;
                if (ty < 0 || ty >= _height)
                    continue;
                for (var col = 0; col < width; col++)
                {
                    var tx = dx + col;
                    if (tx < 0 || tx >= _width)
                        continue;
                    Buffer.BlockCopy(src, row * srcStride + col * 4, _pixels, ty * dstStride + tx * 4, 4);
                }
            }
        }
    }

    public void FrameBufferUpdate() => QueueUiSync(resized: false);

    private void QueueUiSync(bool resized)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_uiUpdateQueued && !resized)
                return;
            _uiUpdateQueued = true;
        }

        Dispatcher.UIThread.Post(() =>
        {
            int width;
            int height;
            byte[] snapshot;

            lock (_gate)
            {
                _uiUpdateQueued = false;
                if (_disposed || _width <= 0 || _height <= 0)
                    return;

                width = _width;
                height = _height;
                snapshot = new byte[_pixels.Length];
                Buffer.BlockCopy(_pixels, 0, snapshot, 0, _pixels.Length);

                if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
                {
                    _bitmap?.Dispose();
                    _bitmap = new WriteableBitmap(
                        new PixelSize(width, height),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Opaque);
                }

                using var fb = _bitmap.Lock();
                var dstStride = fb.RowBytes;
                var srcStride = width * 4;
                for (var row = 0; row < height; row++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        snapshot,
                        row * srcStride,
                        IntPtr.Add(fb.Address, row * dstStride),
                        srcStride);
                }
            }

            if (resized)
                DesktopResized?.Invoke(width, height);
            FramePresented?.Invoke();
        });
    }

    public void Dispose()
    {
        WriteableBitmap? frame;
        WriteableBitmap? cursor;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            frame = _bitmap;
            cursor = _cursorBitmap;
            _bitmap = null;
            _cursorBitmap = null;
            _pendingCursorBgra = null;
            _pixels = Array.Empty<byte>();
        }

        try { frame?.Dispose(); } catch { /* ignore */ }
        try { cursor?.Dispose(); } catch { /* ignore */ }
    }
}
