using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Sentrychan.UI.Converters;

public class StringToBrushConverter : IValueConverter
{
    public static readonly StringToBrushConverter Instance = new();

    public object? Convert(
        object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            try { return SolidColorBrush.Parse(hex); }
            catch { }
        }
        return Brushes.Gray;
    }

    public object? ConvertBack(
        object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}