using Avalonia;
using Avalonia.Controls;
using XcpNgCenter.Shell.Controls;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

[Collection("Infrastructure rendering")]
public sealed class ConsoleFoldLayoutTests
{
    [Theory]
    [InlineData(420, 420, 420)]
    [InlineData(0, 420, 420)]
    [InlineData(400, 420, 400)]
    [InlineData(480, 0, 480)]
    public void HostStaysInsideTheVisibleFold(double viewport, double arranged, double expected)
    {
        Assert.Equal(expected, ConsoleFoldLayout.SelectFoldHeight(viewport, arranged));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(double.NaN, double.PositiveInfinity)]
    public void UnusableMeasurementsDoNotLockAHeight(double viewport, double arranged)
    {
        Assert.Null(ConsoleFoldLayout.SelectFoldHeight(viewport, arranged));
    }

    [Fact]
    public void ScaledHostFillsAShortFoldInsteadOfTheOldFixedHeight()
    {
        var (fold, console, details) = ArrangeConsoleFold(viewportHeight: 480);
        var height = ConsoleFoldLayout.SelectHostHeight(fold, fold, scaleToFit: true, nativeDesktopDipHeight: 1024);

        Assert.NotNull(height);
        Assert.Equal(fold, height);
        Assert.True(height < 600);
        console.Height = height.Value;
        Assert.True(console.Height + details.Height > fold);
    }

    [Fact]
    public void NativeHostGrowsPastTheFoldWhenTheDesktopIsTaller()
    {
        const double fold = 420;
        var native = ConsoleFoldLayout.NativeHostDipHeight(desktopPixels: 768, renderScaling: 1);
        var height = ConsoleFoldLayout.SelectHostHeight(fold, fold, scaleToFit: false, native);

        Assert.Equal(768 + ConsoleFoldLayout.EmbeddedChrome, native);
        Assert.NotNull(height);
        Assert.True(height > fold);
        Assert.Equal(native, height);
    }

    [Fact]
    public void NativeHostUsesDipsAndStaysAtLeastTheFold()
    {
        var shortDesktop = ConsoleFoldLayout.NativeHostDipHeight(200, renderScaling: 2);
        var height = ConsoleFoldLayout.SelectHostHeight(420, 420, scaleToFit: false, shortDesktop);

        Assert.Equal(100 + ConsoleFoldLayout.EmbeddedChrome, shortDesktop);
        Assert.Equal(420, height);
    }

    private static (double Fold, Border Console, Border Details) ArrangeConsoleFold(double viewportHeight)
    {
        var scroll = new Border { Width = 800, Height = viewportHeight };
        var stack = new StackPanel();
        var console = new Border();
        var details = new Border { Height = 160 };
        stack.Children.Add(console);
        stack.Children.Add(details);
        scroll.Child = stack;
        scroll.Measure(new Size(800, viewportHeight));
        scroll.Arrange(new Rect(0, 0, 800, viewportHeight));
        return (scroll.Bounds.Height, console, details);
    }
}
