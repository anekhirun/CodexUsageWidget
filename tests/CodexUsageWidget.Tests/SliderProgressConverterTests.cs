using System.Globalization;
using Xunit;

namespace CodexUsageWidget.Tests;

public class SliderProgressConverterTests
{
    [Theory]
    [InlineData(79, 0, 0)]
    [InlineData(79, 1, 79d / 3)]
    [InlineData(79, 2, 158d / 3)]
    [InlineData(79, 3, 79)]
    [InlineData(118.5, 3, 118.5)]
    [InlineData(79, 4, 79)]
    [InlineData(79, -1, 0)]
    public void FillUsesWholeTrackWidth(double width, double value, double expected)
    {
        var converter = new SliderProgressConverter();
        var actual = converter.Convert([width, value, 0d, 3d], typeof(double), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, (double)actual, 8);
    }
}
