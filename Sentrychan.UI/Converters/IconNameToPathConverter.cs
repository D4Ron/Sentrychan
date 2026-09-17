using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using System;
using System.Globalization;

namespace Sentrychan.UI.Converters;

public class IconNameToPathConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string iconName)
        {
            if (Application.Current!.Resources.TryGetResource(iconName, null, out var pathData))
            {
                return pathData;
            }
            // Fallback to a default icon if not found
            if (Application.Current.Resources.TryGetResource("ChevronRight", null, out var fallback))
            {
                return fallback;
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
