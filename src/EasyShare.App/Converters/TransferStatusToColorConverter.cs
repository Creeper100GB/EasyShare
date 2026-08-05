using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using EasyShare.Core.Models;

namespace EasyShare.App.Converters;

public class TransferStatusToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is TransferStatus status ? status switch
        {
            TransferStatus.Pending => new SolidColorBrush(Color.FromRgb(128, 128, 128)),
            TransferStatus.Active => new SolidColorBrush(Colors.LimeGreen),
            TransferStatus.Completed => new SolidColorBrush(Colors.DodgerBlue),
            TransferStatus.Cancelled => new SolidColorBrush(Colors.Orange),
            TransferStatus.Failed => new SolidColorBrush(Colors.IndianRed),
            TransferStatus.Rejected => new SolidColorBrush(Colors.OrangeRed),
            _ => new SolidColorBrush(Colors.Gray),
        } : new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
