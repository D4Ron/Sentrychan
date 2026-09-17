using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Sentrychan.UI.Converters;

public class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b
                ? new SolidColorBrush(Color.Parse("#00C853"))
                : new SolidColorBrush(Color.Parse("#3A3A60"));
        return new SolidColorBrush(Color.Parse("#3A3A60"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
