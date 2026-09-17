using Avalonia.Data.Converters;
using Sentrychan.Core.Models;
using System;
using System.Globalization;

namespace Sentrychan.UI.Converters;

public class JobStatusToRetryVisibleConverter : IValueConverter
{
    public static readonly JobStatusToRetryVisibleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is JobStatus status)
        {
            return status == JobStatus.Failed;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
