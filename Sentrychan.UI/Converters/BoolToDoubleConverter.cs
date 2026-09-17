using Avalonia.Data.Converters;
using System;
using System.Globalization;
using System.Linq;

namespace Sentrychan.UI.Converters;

public class BoolToDoubleConverter : IValueConverter
{
    public double TrueValue { get; set; } = 1.0;
    public double FalseValue { get; set; } = 0.0;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            if (parameter is string s)
            {
                var parts = s.Split('|');
                if (parts.Length == 2)
                {
                    var valTrue = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    var valFalse = double.Parse(parts[1], CultureInfo.InvariantCulture);
                    return b ? valTrue : valFalse;
                }
            }
            return b ? TrueValue : FalseValue;
        }
        return 0.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
