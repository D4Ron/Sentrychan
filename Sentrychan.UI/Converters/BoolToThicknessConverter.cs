using Avalonia.Data.Converters;
using Avalonia;
using System;
using System.Globalization;

namespace Sentrychan.UI.Converters;

public class BoolToThicknessConverter : IValueConverter
{
    public Thickness TrueValue { get; set; } = new Thickness(1.5);
    public Thickness FalseValue { get; set; } = new Thickness(0);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? TrueValue : FalseValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
