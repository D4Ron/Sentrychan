using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Sentrychan.UI.ViewModels.Mihon;

/// <summary>A selected library card gets an accent border; an unselected one none.</summary>
public sealed class SelectionBrush : IValueConverter
{
    public static readonly SelectionBrush Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true) return Brushes.Transparent;
        // Resources declared in Styles are only found through TryGetResource with the theme.
        var app = Application.Current;
        return app != null && app.TryGetResource("AccentBrush", app.ActualThemeVariant, out var brush) && brush is IBrush b
            ? b
            : Brushes.MediumPurple;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
