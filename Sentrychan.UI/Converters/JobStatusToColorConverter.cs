using Avalonia.Data.Converters;
using Avalonia.Media;
using Sentrychan.Core.Models;
using System;
using System.Globalization;

namespace Sentrychan.UI.Converters;

public class JobStatusToColorConverter : IValueConverter
{
    public static readonly JobStatusToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is JobStatus status)
        {
            return status switch
            {
                JobStatus.Pending => SolidColorBrush.Parse("#FFC107"),
                JobStatus.Downloading => SolidColorBrush.Parse("#007ACC"),
                JobStatus.Completed => SolidColorBrush.Parse("#28A745"),
                JobStatus.Failed => SolidColorBrush.Parse("#DC3545"),
                _ => Brushes.Gray
            };
        }
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
