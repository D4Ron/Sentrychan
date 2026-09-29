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
        // The icon geometries are declared in GlobalStyles' resources. Application.Resources
        // alone doesn't include styles, so every lookup missed and each nav icon drew nothing;
        // the application's own TryGetResource searches its styles too.
        if (value is string iconName && Application.Current is { } app)
        {
            if (app.TryGetResource(iconName, app.ActualThemeVariant, out var pathData))
                return pathData;
            // Fallback to a default icon if not found
            if (app.TryGetResource("ChevronRight", app.ActualThemeVariant, out var fallback))
                return fallback;
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
