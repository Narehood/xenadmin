using System.Runtime.InteropServices;
using Avalonia.Threading;
using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

[Collection("Infrastructure rendering")]
public sealed class FramebufferCopyRectangleTests
{
    [Theory]
    [InlineData(1, 1, 4, 3, 2, 1)] // right
    [InlineData(2, 1, 4, 3, 1, 1)] // left
    [InlineData(1, 1, 4, 3, 1, 2)] // down
    [InlineData(1, 2, 4, 3, 1, 1)] // up
    [InlineData(1, 1, 4, 3, 2, 2)]
    [InlineData(2, 2, 4, 3, 1, 1)]
    [InlineData(1, 2, 4, 3, 2, 1)]
    [InlineData(2, 1, 4, 3, 1, 2)]
    [InlineData(1, 1, 4, 3, 1, 1)]
    [InlineData(-2, -1, 5, 4, 1, 1)] // source outside left/top
    [InlineData(4, 3, 5, 4, 1, 1)] // source outside right/bottom
    [InlineData(1, 1, 5, 4, -2, -1)] // destination outside left/top
    [InlineData(1, 1, 5, 4, 4, 3)] // destination outside right/bottom
    [InlineData(-1, 1, 6, 4, 0, 0)] // clip while overlapping
    [InlineData(0, -1, 6, 5, 0, 0)]
    [InlineData(20, 20, 4, 3, 1, 1)] // zeroes from fully offscreen source
    [InlineData(1, 1, 4, 3, 20, 20)] // invisible destination
    [InlineData(0, 0, 0, 4, 1, 1)]
    [InlineData(0, 0, 4, -1, 1, 1)]
    [InlineData(int.MinValue, int.MinValue, 6, 5, 0, 0)]
    [InlineData(0, 0, 6, 5, int.MaxValue, int.MaxValue)]
    [InlineData(0, 0, int.MaxValue, int.MaxValue, 0, 0)]
    public void ActualPixelsMatchSnapshotSemanticsWithOverlapAndClipping(int x, int y, int width, int height, int dx, int dy)
    {
        using var framebuffer = new AvaloniaRfbFramebuffer("Test", "test");
        var initial = InitialPixels();
        framebuffer.DesktopSize(6, 5);
        framebuffer.DrawImage(initial, 0, 24, 0, 0, 6, 5);
        framebuffer.CopyRectangle(x, y, width, height, dx, dy);
        framebuffer.FrameBufferUpdate();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Expected(initial, x, y, width, height, dx, dy), ReadPixels(framebuffer));
    }

    [Fact]
    public void DeterministicMixedCopiesKeepTheSameResultAsAnIndependentSnapshot()
    {
        using var framebuffer = new AvaloniaRfbFramebuffer("Test", "test");
        var expected = InitialPixels();
        framebuffer.DesktopSize(6, 5);
        framebuffer.DrawImage(expected, 0, 24, 0, 0, 6, 5);
        var random = new Random(627);
        for (var index = 0; index < 100; index++)
        {
            var x = random.Next(-3, 8);
            var y = random.Next(-3, 7);
            var dx = random.Next(-3, 8);
            var dy = random.Next(-3, 7);
            var width = random.Next(1, 9);
            var height = random.Next(1, 8);
            expected = Expected(expected, x, y, width, height, dx, dy);
            framebuffer.CopyRectangle(x, y, width, height, dx, dy);
            framebuffer.FrameBufferUpdate();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(expected, ReadPixels(framebuffer));
        }
    }

    [Fact]
    public void RepeatedScrollCopiesDoNotAllocateRectangleSizedBuffers()
    {
        using var framebuffer = new AvaloniaRfbFramebuffer("Test", "test");
        framebuffer.DesktopSize(1920, 1080);
        Dispatcher.UIThread.RunJobs();
        for (var index = 0; index < 5; index++) framebuffer.CopyRectangle(0, 1, 1920, 1079, 0, 0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10; index++) framebuffer.CopyRectangle(0, 1, 1920, 1079, 0, 0);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static byte[] InitialPixels() => Enumerable.Range(0, 6 * 5 * 4).Select(value => (byte)(value + 1)).ToArray();

    private static byte[] Expected(byte[] initial, int x, int y, int width, int height, int dx, int dy)
    {
        var expected = (byte[])initial.Clone();
        // Iterate the fixed destination image, using an untouched snapshot as
        // the oracle. This handles huge/offscreen requests without large loops.
        for (var row = 0; row < 5; row++)
        for (var column = 0; column < 6; column++)
        {
            var relativeX = (long)column - dx;
            var relativeY = (long)row - dy;
            if (relativeX < 0 || relativeX >= width || relativeY < 0 || relativeY >= height) continue;
            var sourceX = x + relativeX;
            var sourceY = y + relativeY;
            for (var channel = 0; channel < 4; channel++)
                expected[(row * 6 + column) * 4 + channel] = sourceX is >= 0 and < 6 && sourceY is >= 0 and < 5
                    ? initial[(int)((sourceY * 6 + sourceX) * 4 + channel)] : (byte)0;
        }
        return expected;
    }

    private static byte[] ReadPixels(AvaloniaRfbFramebuffer framebuffer)
    {
        using var bitmap = framebuffer.Bitmap!.Lock();
        var pixels = new byte[6 * 5 * 4];
        for (var row = 0; row < 5; row++)
            Marshal.Copy(IntPtr.Add(bitmap.Address, row * bitmap.RowBytes), pixels, row * 24, 24);
        return pixels;
    }
}
