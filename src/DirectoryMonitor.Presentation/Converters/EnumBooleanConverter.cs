using System.Globalization;
using System.Windows.Data;

namespace DirectoryMonitor.Presentation.Converters;

public sealed class EnumBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value.Equals(Enum.Parse(value.GetType(), parameter.ToString()!));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => (bool)value ? Enum.Parse(targetType, parameter.ToString()!) : System.Windows.Data.Binding.DoNothing;
}
