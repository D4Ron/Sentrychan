using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Sentrychan.Core.Models;

namespace Sentrychan.UI.Converters;

public class MonitoringStateToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MonitoringState state)
        {
            return state == MonitoringState.Active;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked)
        {
            return isChecked ? MonitoringState.Active : MonitoringState.Paused;
        }
        return MonitoringState.Paused;
    }
}
