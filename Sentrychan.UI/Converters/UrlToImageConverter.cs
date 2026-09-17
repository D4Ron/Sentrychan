using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;

namespace Sentrychan.UI.Converters;

public class UrlToImageConverter : IValueConverter
{
    public static readonly UrlToImageConverter Instance = new();
    private static readonly HttpClient _http = new();
    private static readonly Dictionary<string, Bitmap> _cache = new();

    public object? Convert(
        object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrEmpty(url))
            return null;

        // Return cached bitmap immediately if available
        if (_cache.TryGetValue(url, out var cached))
            return cached;

        return null; // Return null first, then load async
    }

    public object? ConvertBack(
        object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}