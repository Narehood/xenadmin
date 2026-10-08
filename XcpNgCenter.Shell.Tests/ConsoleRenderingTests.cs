using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using XcpNgCenter.Rfb;
using XcpNgCenter.Shell.Controls;
using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

[Collection("Infrastructure rendering")]
public sealed class ConsoleRenderingTests(InfrastructureRenderingFixture rendering)
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public Task FirstFrameCanRenderAtFractionalConsoleScale(double displayScale)
        => rendering.Run(() =>
        {
            using var framebuffer = new AvaloniaRfbFramebuffer("Synthetic console", "synthetic");
            var view = new RfbConsoleView();
            var interpolationBefore = RenderOptions.GetBitmapInterpolationMode(view);
            var window = new Window { Width = 640, Height = 480, Content = view, WindowDecorations = WindowDecorations.None };
            try
            {
                window.Show();
                window.SetRenderScaling(displayScale);
                framebuffer.FramePresented += () => view.Frame = framebuffer.Bitmap;
                framebuffer.DesktopSize(1920, 1080);
                framebuffer.FillRectangle(0, 0, 1920, 1080, new RfbColor(40, 80, 160));
                framebuffer.FrameBufferUpdate();
                Dispatcher.UIThread.RunJobs();

                // This exercises the compositor, which rejects visual invalidation
                // from Render; drawing into an isolated bitmap misses that failure.
                using var captured = window.CaptureRenderedFrame();
                Assert.NotNull(captured);
                Assert.Equal(interpolationBefore, RenderOptions.GetBitmapInterpolationMode(view));
            }
            finally
            {
                view.Frame = null;
                window.Close();
            }
        });

    [Fact]
    public Task FrameResizeAndFitChangesCanRenderInTheSameWindow()
        => rendering.Run(() =>
        {
            using var framebuffer = new AvaloniaRfbFramebuffer("Synthetic console", "synthetic");
            var view = new RfbConsoleView();
            var interpolationBefore = RenderOptions.GetBitmapInterpolationMode(view);
            var window = new Window { Width = 640, Height = 480, Content = view, WindowDecorations = WindowDecorations.None };
            try
            {
                window.Show();
                framebuffer.FramePresented += () => view.Frame = framebuffer.Bitmap;
                foreach (var (width, height, fit) in new[]
                {
                    (320, 240, true), (1920, 1080, true),
                    (1920, 1080, false), (800, 600, true), (320, 240, true)
                })
                {
                    view.ScaleToFit = fit;
                    framebuffer.DesktopSize(width, height);
                    framebuffer.FillRectangle(0, 0, width, height, new RfbColor(40, 80, 160));
                    framebuffer.FrameBufferUpdate();
                    Dispatcher.UIThread.RunJobs();
                    using var captured = window.CaptureRenderedFrame();
                    Assert.NotNull(captured);
                    Assert.Equal(interpolationBefore, RenderOptions.GetBitmapInterpolationMode(view));
                }
            }
            finally
            {
                view.Frame = null;
                window.Close();
            }
        });

    [Theory]
    [InlineData(96, 1)]
    [InlineData(80, 1.25)]
    [InlineData(64, 1.5)]
    [InlineData(48, 2)]
    public Task FractionalPhysicalScaleSmoothsContrastingPixels(double viewport, double displayScale)
        => rendering.Run(() =>
        {
            using var framebuffer = StripedFramebuffer(128);
            var view = new RfbConsoleView { Frame = framebuffer.Bitmap };
            var interpolationBefore = RenderOptions.GetBitmapInterpolationMode(view);
            var window = new Window { Width = viewport, Height = viewport, Content = view, WindowDecorations = WindowDecorations.None };
            try
            {
                window.Show();
                window.SetRenderScaling(displayScale);
                Dispatcher.UIThread.RunJobs();
                using var captured = window.CaptureRenderedFrame();
                Assert.NotNull(captured);
                var pixel = ReadPixel(captured, captured.PixelSize.Width / 2, captured.PixelSize.Height / 2);
                // Nearest-neighbor can only produce black or white from these
                // alternating columns. High-quality shrinking must blend them.
                Assert.InRange(pixel[0], (byte)16, (byte)239);
                Assert.Equal(pixel[0], pixel[1]);
                Assert.Equal(pixel[0], pixel[2]);
                Assert.Equal(255, pixel[3]);
                Assert.Equal(interpolationBefore, RenderOptions.GetBitmapInterpolationMode(view));
                Assert.Equal(EdgeMode.Aliased, RenderOptions.GetEdgeMode(view));
            }
            finally
            {
                view.Frame = null;
                window.Close();
            }
        });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    public Task WholePhysicalPixelScaleKeepsContrastingPixelsSharp(double displayScale)
        => rendering.Run(() =>
        {
            using var framebuffer = StripedFramebuffer(64);
            var view = new RfbConsoleView { Frame = framebuffer.Bitmap };
            var interpolationBefore = RenderOptions.GetBitmapInterpolationMode(view);
            var window = new Window { Width = 128, Height = 128, Content = view, WindowDecorations = WindowDecorations.None };
            try
            {
                window.Show();
                window.SetRenderScaling(displayScale);
                Dispatcher.UIThread.RunJobs();
                using var captured = window.CaptureRenderedFrame();
                Assert.NotNull(captured);
                var x = captured.PixelSize.Width / 2;
                var y = captured.PixelSize.Height / 2;
                Assert.Equal(new byte[] { 0, 0, 0, 255 }, ReadPixel(captured, x, y));
                Assert.Equal(new byte[] { 255, 255, 255, 255 }, ReadPixel(captured, x + 2, y));
                Assert.Equal(interpolationBefore, RenderOptions.GetBitmapInterpolationMode(view));
                Assert.Equal(EdgeMode.Aliased, RenderOptions.GetEdgeMode(view));
            }
            finally
            {
                view.Frame = null;
                window.Close();
            }
        });

    private static AvaloniaRfbFramebuffer StripedFramebuffer(int size)
    {
        var framebuffer = new AvaloniaRfbFramebuffer("Striped console", "synthetic");
        framebuffer.DesktopSize(size, size);
        for (var x = 0; x < size; x++)
        {
            var value = (byte)(x % 2 == 0 ? 0 : 255);
            framebuffer.FillRectangle(x, 0, 1, size, new RfbColor(value, value, value));
        }
        framebuffer.FrameBufferUpdate();
        Dispatcher.UIThread.RunJobs();
        return framebuffer;
    }

    private static byte[] ReadPixel(Bitmap bitmap, int x, int y)
    {
        var pixel = new byte[4];
        var pinned = GCHandle.Alloc(pixel, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(x, y, 1, 1), pinned.AddrOfPinnedObject(), pixel.Length, pixel.Length); }
        finally { pinned.Free(); }
        return pixel;
    }
}
