using System.IO;
using System.Windows;
using EasyShare.App.Localization;
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
        SpeedLimitText.Text = SpeedLimitSlider.Value == 0 ? Loc.Tr("Settings.Unlimited") : $"{SpeedLimitSlider.Value:F0} MB/s";

        var themeIndex = _config.Theme switch
        {
            Theme.Light => 0,
            Theme.Dark => 1,
            _ => 2,
        };
        ThemeComboBox.SelectedIndex = themeIndex;

        LanguageComboBox.SelectedIndex = _config.Language == "en" ? 1 : 0;

        UpdateContextMenuButton();
    }

    private void UpdateContextMenuButton()
    {
        ContextMenuButton.Content = ShellIntegration.IsRegistered()
            ? Loc.Tr("Settings.ContextMenuRemove")
            : Loc.Tr("Settings.ContextMenu");
    }

    private void BrowseSavePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Alle Dateien|*.*", Title = Loc.Tr("Settings.SavePath") };
        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FileName))
            SavePathTextBox.Text = Path.GetDirectoryName(dialog.FileName) ?? dialog.FileName;
    }

    private void SpeedLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SpeedLimitText.Text = e.NewValue == 0 ? Loc.Tr("Settings.Unlimited") : $"{e.NewValue:F0} MB/s";
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

    private void LanguageComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        var lang = LanguageComboBox.SelectedIndex == 1 ? "en" : "de";
        _config.Language = lang;
        Loc.Instance.Language = lang;

        UpdateContextMenuButton();
    }

    private void ContextMenuButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = Environment.ProcessPath
                ?? System.Reflection.Assembly.GetEntryAssembly()?.Location
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
            if (!ShellIntegration.IsRegistered())
            {
                ShellIntegration.Register(exePath, Loc.Tr("Shell.ShareMenu"));
                ContextMenuButton.Content = Loc.Tr("Settings.ContextMenuRemove");
            }
            else
            {
                ShellIntegration.Unregister();
                ContextMenuButton.Content = Loc.Tr("Settings.ContextMenu");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.Tr("Settings.Error", ex.Message), "EasyShare", MessageBoxButton.OK, MessageBoxImage.Error);
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
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[EasyShare] SaveSettings failed: {ex.Message}"); }

        Close();
    }

    private static void SetAutoStart(bool enable)
    {
        var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null) return;

        if (enable)
        {
            var exe = Environment.ProcessPath ?? "";
            key.SetValue("EasyShare", $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue("EasyShare", false);
        }
    }
}
