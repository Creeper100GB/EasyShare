using System.Globalization;
using System.Windows;
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
            TransferStatus.Pending => Resolve("TextSecondary"),
            TransferStatus.Active => Resolve("SuccessColor"),
            TransferStatus.Completed => Resolve("AccentGradient2"),
            TransferStatus.Cancelled => Resolve("WarningColor"),
            TransferStatus.Failed => Resolve("ErrorColor"),
            TransferStatus.Rejected => Resolve("ErrorColor"),
            _ => Resolve("TextSecondary"),
        } : Resolve("TextSecondary");
    }

    private static object Resolve(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(Colors.Gray);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
