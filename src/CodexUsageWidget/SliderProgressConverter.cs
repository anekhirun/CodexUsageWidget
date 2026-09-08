using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CodexUsageWidget;

public sealed class SliderProgressConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 4 || values.Any(value => value is not double number || !double.IsFinite(number)))
            return 0d;
        double width = (double)values[0];
        double value = (double)values[1];
        double minimum = (double)values[2];
        double maximum = (double)values[3];
        return maximum <= minimum ? 0d : Math.Max(0, width) * Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => DependencyProperty.UnsetValue).ToArray();
}
