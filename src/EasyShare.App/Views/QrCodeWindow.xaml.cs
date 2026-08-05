using System.Net;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace EasyShare.App.Views;

public partial class QrCodeWindow : Wpf.Ui.Controls.FluentWindow
{
    public QrCodeWindow(string localIp, int port)
    {
        InitializeComponent();
        var url = $"http://{localIp}:{port}";
        UrlText.Text = url;

        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrCodeData);
        var qrCodeImage = qrCode.GetGraphic(20);

        var image = new BitmapImage();
        using var ms = new System.IO.MemoryStream(qrCodeImage);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();

        QrImage.Source = image;
    }
}
