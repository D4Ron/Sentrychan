using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Sentrychan.UI.Converters;

/// <summary>true (long-strip) → "Webtoon", false (paged) → "Paged". Labels the reader mode toggle.</summary>
public class BoolToReaderModeConverter : IValueConverter
{
    public static readonly BoolToReaderModeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Webtoon" : "Paged";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
