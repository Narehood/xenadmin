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
    public void DefaultDetailFoldDoesNotKeepTheOldFixedConsole()
    {
        // Toolbar plus a short remainder, as on the default 1280×800 window.
        // The previous host was locked to 600px, which is taller than this fold.
        var host = new Grid
        {
            Width = 960,
            Height = 560,
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        var toolbar = new Border { Height = 68 };
        var fold = new Border();
        Grid.SetRow(fold, 1);
        host.Children.Add(toolbar);
        host.Children.Add(fold);

        host.Measure(new Size(960, 560));
        host.Arrange(new Rect(0, 0, 960, 560));

        var height = ConsoleFoldLayout.SelectFoldHeight(fold.Bounds.Height, fold.Bounds.Height);

        Assert.NotNull(height);
        Assert.InRange(height.Value, fold.Bounds.Height - 0.5, fold.Bounds.Height + 0.5);
        Assert.True(height.Value < 600);
    }
}
