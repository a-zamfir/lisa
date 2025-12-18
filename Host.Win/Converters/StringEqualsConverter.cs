// File: Host.Win/Converters/StringEqualsConverter.cs
using System;
using System.Globalization;
using System.Windows.Data;

namespace Host.Win.Converters
{
    /// <summary>
    /// Compares a string value to a converter parameter for RadioButton bindings.
    /// </summary>
    public sealed class StringEqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var current = value as string;
            var target = parameter as string;
            return string.Equals(current, target, StringComparison.OrdinalIgnoreCase);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter is string s)
            {
                return s;
            }
            return System.Windows.Data.Binding.DoNothing;
        }
    }
}
