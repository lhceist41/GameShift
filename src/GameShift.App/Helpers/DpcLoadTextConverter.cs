using System.Globalization;
using System.Windows.Data;
using GameShift.Core.Monitoring;

namespace GameShift.App.Helpers;

/// <summary>
/// Shows a stored DPC value from session history as DPC load.
/// </summary>
public class DpcLoadTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double stored ? DpcLoad.Format(stored) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
