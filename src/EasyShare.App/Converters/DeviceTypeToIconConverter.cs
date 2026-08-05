using System.Globalization;
using System.Windows.Data;
using EasyShare.Core.Models;

namespace EasyShare.App.Converters;

public class DeviceTypeToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is DeviceType type ? type switch
        {
            DeviceType.Desktop => "\U0001F5A5",
            DeviceType.Mobile => "\U0001F4F1",
            DeviceType.Web => "\U0001F310",
            DeviceType.Headless => "\U0001F3A8",
            DeviceType.Server => "\U0001F4BB",
            _ => "\U0001F4BB",
        } : "\U0001F4BB";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
