using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Sentrychan.UI.Converters;

public class StringToInitialConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            return s.Trim().Substring(0, 1).ToUpperInvariant();
        }
        return "?";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
