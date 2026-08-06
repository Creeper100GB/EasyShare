using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace EasyShare.App.Views;

public partial class QrCodeWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _url;

    public QrCodeWindow(string localIp, int port)
    {
        InitializeComponent();
        var host = localIp.Contains(':') ? $"[{localIp}]" : localIp;
        _url = $"https://{host}:{port}";
        UrlText.Text = _url;

        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(_url, QRCodeGenerator.ECCLevel.M);
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

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_url);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
        }
        catch { }
    }
}