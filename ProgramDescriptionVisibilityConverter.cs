using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AeroStart;

public sealed class ProgramDescriptionVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var description = (values.ElementAtOrDefault(0) as string)?.Trim();
        var name = (values.ElementAtOrDefault(1) as string)?.Trim();
        return string.IsNullOrWhiteSpace(description)
            || description == "."
            || string.Equals(description, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(description, "Windows-Start", StringComparison.OrdinalIgnoreCase)
            || string.Equals(description, "Windows-Start (nur gelesen)", StringComparison.OrdinalIgnoreCase)
            || string.Equals(description, "Lokal in AeroStart", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
