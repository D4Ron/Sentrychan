using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Sentrychan.UI.ViewModels.Mihon;

/// <summary>Read chapters are dimmed, as in Mihon's lists.</summary>
public sealed class ReadOpacity : IValueConverter
{
    public static readonly ReadOpacity Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 0.45 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
