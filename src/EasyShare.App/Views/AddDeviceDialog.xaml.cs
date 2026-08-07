using System.Net.Http;
using System.Text.Json;
using System.Windows;
using EasyShare.App.Localization;

namespace EasyShare.App.Views;

public partial class AddDeviceDialog : Wpf.Ui.Controls.FluentWindow
{
    public string DeviceAlias { get; private set; } = string.Empty;
    public string IpAddress { get; private set; } = string.Empty;
    public int Port { get; private set; }
    public string Fingerprint { get; private set; } = string.Empty;
    public string DeviceModel { get; private set; } = string.Empty;

    public AddDeviceDialog()
    {
        InitializeComponent();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var ip = IpTextBox.Text.Trim();
        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            StatusText.Text = Loc.Tr("Add.InvalidIp");
            return;
        }

        if (!int.TryParse(PortTextBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            StatusText.Text = Loc.Tr("Settings.PortInvalid");
            return;
        }

        ConnectButton.IsEnabled = false;
        ConnectButton.Content = Loc.Tr("Add.Checking");
        StatusText.Text = Loc.Tr("Add.Checking");

        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                UseProxy = false,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var json = await client.GetStringAsync($"https://{ip}:{port}/api/localsend/v2/info");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var fingerprint = root.TryGetProperty("fingerprint", out var f) ? f.GetString() : null;
            if (string.IsNullOrEmpty(fingerprint))
            {
                StatusText.Text = Loc.Tr("Add.DeviceNotEasyShare");
                return;
            }

            DeviceAlias = root.TryGetProperty("alias", out var a) && !string.IsNullOrEmpty(a.GetString())
                ? a.GetString()!
                : Loc.Tr("Main.DeviceUnknown");
            DeviceModel = root.TryGetProperty("deviceModel", out var m) && !string.IsNullOrEmpty(m.GetString())
                ? m.GetString()!
                : ip;
            var announced = root.TryGetProperty("port", out var p) ? p.GetInt32() : port;
            IpAddress = ip;
            Port = announced > 0 ? announced : port;
            Fingerprint = fingerprint;

            DialogResult = true;
            Close();
        }
        catch
        {
            StatusText.Text = Loc.Tr("Add.Failed");
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = Loc.Tr("Add.Connect");
        }
    }
}