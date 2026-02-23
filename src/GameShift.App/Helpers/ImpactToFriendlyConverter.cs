using System.Globalization;
using System.Windows.Data;

namespace GameShift.App.Helpers;

public class ImpactToFriendlyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() switch
        {
            "High" => "Big improvement",
            "Medium" => "Moderate improvement",
            "Low" => "Small improvement",
            _ => ""
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
