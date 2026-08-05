using System.IO;
using System.Windows;
using EasyShare.Core.Config;
using EasyShare.Shell;
using Microsoft.Win32;

namespace EasyShare.App.Views;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AppConfig _config;
    private readonly string _configPath;

    public SettingsWindow(AppConfig config, string configPath)
    {
        InitializeComponent();
        _config = config;
        _configPath = configPath;

        AliasTextBox.Text = _config.DeviceAlias;
        PortBox.Text = _config.HttpPort.ToString();
        SavePathTextBox.Text = _config.DefaultSavePath;
        AutoAcceptCheckBox.IsChecked = _config.AutoAcceptTrusted;
        AutoStartCheckBox.IsChecked = _config.AutoStart;
        SpeedLimitSlider.Value = _config.SpeedLimitBytesPerSecond == 0 ? 0 : _config.SpeedLimitBytesPerSecond / 1_000_000.0;

        var themeIndex = _config.Theme switch
        {
            Theme.Light => 0,
            Theme.Dark => 1,
            _ => 2,
        };
        ThemeComboBox.SelectedIndex = themeIndex;
    }

    private void BrowseSavePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Alle Dateien|*.*", Title = "Speicherort wählen" };
        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FileName))
            SavePathTextBox.Text = Path.GetDirectoryName(dialog.FileName) ?? dialog.FileName;
    }

    private void SpeedLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SpeedLimitText.Text = e.NewValue == 0 ? "Unbegrenzt" : $"{e.NewValue:F0} MB/s";
    }

    private void ThemeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var theme = ThemeComboBox.SelectedIndex switch
        {
            0 => Theme.Light,
            1 => Theme.Dark,
            _ => Theme.Auto,
        };

        _config.Theme = theme;

        if (Application.Current.MainWindow is MainWindow mw)
            mw.ApplyTheme(theme);
    }

    private void ContextMenuButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = System.Reflection.Assembly.GetEntryAssembly()?.Location
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
            if (!ShellIntegration.IsRegistered())
            {
                ShellIntegration.Register(exePath);
                ContextMenuButton.Content = "Kontextmenü entfernt";
            }
            else
            {
                ShellIntegration.Unregister();
                ContextMenuButton.Content = "Explorer-Kontextmenü registrieren";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fehler: {ex.Message}", "EasyShare", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _config.DeviceAlias = AliasTextBox.Text;
        _config.HttpPort = int.TryParse(PortBox.Text, out var port) ? port : 53317;
        _config.DefaultSavePath = SavePathTextBox.Text;
        _config.AutoAcceptTrusted = AutoAcceptCheckBox.IsChecked == true;
        _config.AutoStart = AutoStartCheckBox.IsChecked == true;

        var speedLimit = SpeedLimitSlider.Value;
        _config.SpeedLimitBytesPerSecond = speedLimit == 0 ? 0 : (int)(speedLimit * 1_000_000);

        SetAutoStart(_config.AutoStart);

        try
        {
            var dir = Path.GetDirectoryName(_configPath)!;
            Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(_config);
            File.WriteAllText(_configPath, json);
        }
        catch { }

        Close();
    }

    private static void SetAutoStart(bool enable)
    {
        var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null) return;

        if (enable)
        {
            var exe = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
            key.SetValue("EasyShare", $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue("EasyShare", false);
        }
    }
}
