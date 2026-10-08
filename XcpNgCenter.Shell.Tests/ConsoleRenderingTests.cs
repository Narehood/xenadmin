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
                Assert.Equal(BitmapInterpolationMode.None, RenderOptions.GetBitmapInterpolationMode(view));
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
                    Assert.Equal(BitmapInterpolationMode.None, RenderOptions.GetBitmapInterpolationMode(view));
                }
            }
            finally
            {
                view.Frame = null;
                window.Close();
            }
        });
}
