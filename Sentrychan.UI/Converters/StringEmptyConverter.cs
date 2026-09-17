using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Sentrychan.UI.Converters;

public class StringEmptyConverter : IValueConverter
{
    public static readonly StringEmptyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(value as string);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
