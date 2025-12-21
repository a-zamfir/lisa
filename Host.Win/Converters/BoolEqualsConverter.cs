using System;
using System.Globalization;
using System.Windows.Data;

namespace Host.Win.Converters
{
    /// <summary>
    /// Compares a bool value to a converter parameter for RadioButton bindings.
    /// </summary>
    public sealed class BoolEqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (parameter is null) return false;
            if (value is bool b)
            {
                if (bool.TryParse(parameter.ToString(), out var target))
                {
                    return b == target;
                }
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter is not null && bool.TryParse(parameter.ToString(), out var target))
            {
                return target;
            }
            return System.Windows.Data.Binding.DoNothing;
        }
    }
}
