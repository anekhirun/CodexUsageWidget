using System.Drawing;
using Xunit;

namespace CodexUsageWidget.Tests;

public class WidgetPlacementTests
{
    private static readonly Rectangle Screen = new(0, 0, 1920, 1080);
    private static readonly Rectangle Work = new(0, 0, 1920, 1032);

    [Theory]
    [InlineData(100, 0, 1, 100)]
    [InlineData(100, 0, 0.6666666667, 100)]
    [InlineData(2020, 3840, 0.5, 100)]
    [InlineData(-1180, -2560, 0.5, 100)]
    public void MigratesLegacyAbsoluteDipPositionToRelativeDipOffset(double saved, double screenLeft, double dipPerPixel, double expected)
    {
        Assert.Equal(expected, WidgetPlacement.MigrateLegacyOffset(saved, screenLeft, dipPerPixel), 5);
    }

    [Theory]
    [InlineData(279)]
    [InlineData(385)]
    public void BothLayoutsFitAtRightEdge(double width)
    {
        var result = WidgetPlacement.Calculate(Screen, Work, null, 9000, 1.5, width);
        Assert.Equal((int)Math.Round(width * 1.5), result.Width);
        Assert.Equal(Screen.Right, result.Right);
        Assert.True(Screen.Contains(result));
    }

    [Theory]
    [InlineData(-900, 0)]
    [InlineData(177, 177)]
    [InlineData(3846, 1641)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void SavedPositionIsClampedInsideMonitor(double offset, int expectedLeft)
    {
        var result = WidgetPlacement.Calculate(Screen, Work, new(0, 1032, 1920, 48), offset, 1);
        Assert.Equal(new Rectangle(expectedLeft, 1032, 279, 48), result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void MonitorRelativeOffsetScalesWithoutAccumulatingDrift(double scale)
    {
        var screen = new Rectangle(-2560, -240, 2560, 1440);
        var bar = new Rectangle(-2560, 1128, 2560, 72);
        var first = WidgetPlacement.Calculate(screen, screen, bar, 120, scale);
        Assert.Equal(screen.Left + (int)Math.Round(120 * scale), first.Left);
        Assert.Equal((int)Math.Round(279 * scale), first.Width);
        Assert.True(screen.Contains(first));
        for (int i = 0; i < 100; i++)
            Assert.Equal(first, WidgetPlacement.Calculate(screen, screen, bar, 120, scale));
    }

    [Theory]
    [InlineData(1078, 48)]
    [InlineData(1080, 48)]
    [InlineData(1078, 2)]
    [InlineData(-46, 48)]
    public void AutoHiddenTaskbarCannotHideOrCollapseWidget(int barTop, int barHeight)
    {
        var result = WidgetPlacement.Calculate(Screen, Screen, new(0, barTop, 1920, barHeight), 177, 1);
        Assert.Equal(48, result.Height);
        Assert.True(Screen.Contains(result));
    }

    [Fact]
    public void MissingTaskbarHasVisibleFallback()
    {
        Assert.Equal(new Rectangle(177, 1032, 279, 48),
            WidgetPlacement.Calculate(Screen, Screen, null, 177, 1));
    }

    [Fact]
    public void TopTaskbarRemainsAtTop()
    {
        Assert.Equal(0, WidgetPlacement.Calculate(Screen, new(0, 48, 1920, 1032),
            new(0, 0, 1920, 48), 177, 1).Top);
    }

    [Fact]
    public void TaskbarOnAnotherMonitorDoesNotSupplyPosition()
    {
        Assert.Equal(new Rectangle(177, 1032, 279, 48),
            WidgetPlacement.Calculate(Screen, Screen, new(1920, 1392, 2560, 48), 177, 1));
    }

    [Fact]
    public void VerticalTaskbarDoesNotCreateFullHeightWidget()
    {
        var result = WidgetPlacement.Calculate(Screen, Screen, new(0, 0, 60, 1080), 177, 1);
        Assert.Equal(48, result.Height);
        Assert.True(Screen.Contains(result));
    }

    [Fact]
    public void TemporarySmallerMonitorDoesNotDestroyPreferredOffset()
    {
        const double savedOffset = 2100;
        var small = WidgetPlacement.Calculate(Screen, Work, null, savedOffset, 1);
        var restored = WidgetPlacement.Calculate(new(1920, 0, 2560, 1440),
            new(1920, 0, 2560, 1392), null, savedOffset, 1);
        Assert.Equal(1641, small.Left);
        Assert.Equal(4020, restored.Left);
    }
}
