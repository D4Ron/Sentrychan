using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Sentrychan.UI.Converters;

/// <summary>
/// Maps Series.AiringStatus to the colour of the little status dot on a library card:
/// green = airing, violet = finished, amber = not yet aired. Unknown/null yields
/// transparent, which hides the dot without needing a second visibility binding.
/// Colours are literal (not theme brushes) so the dot stays a recognisable traffic
/// light in every theme.
/// </summary>
public class AiringStatusToDotBrushConverter : IValueConverter
{
    public static readonly AiringStatusToDotBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var normalized = Sentrychan.Core.Services.AiringStatusNormalizer.Normalize(value as string);
        return normalized switch
        {
            Sentrychan.Core.Services.AiringStatusNormalizer.Airing      => new SolidColorBrush(Color.Parse("#00C853")),
            Sentrychan.Core.Services.AiringStatusNormalizer.Finished    => new SolidColorBrush(Color.Parse("#8B5CF6")),
            Sentrychan.Core.Services.AiringStatusNormalizer.NotYetAired => new SolidColorBrush(Color.Parse("#FFB300")),
            _ => Brushes.Transparent
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
