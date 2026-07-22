namespace XcpNgCenter.Rfb;

/// <summary>
/// Buffer-oriented RFB framebuffer callbacks (no System.Drawing / WinForms).
/// Pixel buffers passed to <see cref="DrawImage"/> are BGRA32 (little-endian).
/// </summary>
public interface IRfbFramebuffer
{
    string VmName { get; }
    string Uuid { get; }

    void Bell();
    void CopyRectangle(int x, int y, int width, int height, int dx, int dy);
    void CutText(string text);
    void DesktopSize(int width, int height);
    void DrawImage(byte[] bgra, int offset, int stride, int x, int y, int width, int height);
    void FillRectangle(int x, int y, int width, int height, RfbColor color);
    void FrameBufferUpdate();
    void SetCursor(byte[] bgra, int offset, int stride, int hotspotX, int hotspotY, int width, int height);
}
