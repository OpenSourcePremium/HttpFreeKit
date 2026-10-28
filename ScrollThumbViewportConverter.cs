using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace HttpFreeKit;







public sealed class ScrollThumbViewportConverter : IMultiValueConverter
{
    private const double MinThumbLength = 28;


    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double viewport = ToDouble(values, 0, double.NaN);
        if (double.IsNaN(viewport) || double.IsInfinity(viewport))
            return viewport;

        double minimum = ToDouble(values, 1, 0);
        double maximum = ToDouble(values, 2, 0);
        double width = ToDouble(values, 3, 0);
        double height = ToDouble(values, 4, 0);
        bool vertical = values.Length > 5 && values[5] is Orientation o && o == Orientation.Vertical;

        double track = vertical ? height : width;
        double range = Math.Max(0, maximum - minimum);
        if (range <= 0 || track <= MinThumbLength * 2)
            return viewport;


        double needed = MinThumbLength * range / (track - MinThumbLength);
        return Math.Max(viewport, needed);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static double ToDouble(object[] values, int index, double fallback)
        => values.Length > index && values[index] is double d ? d : fallback;
}