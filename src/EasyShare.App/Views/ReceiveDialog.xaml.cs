using System.Windows;
using EasyShare.App.Localization;
using EasyShare.Core.Models;

namespace EasyShare.App.Views;

public partial class ReceiveDialog : Wpf.Ui.Controls.FluentWindow
{
    public bool Accepted { get; private set; }
    public bool TrustDevice { get; private set; }

    private readonly System.Windows.Threading.DispatcherTimer _countdownTimer;
    private int _countdown = 30;

    public ReceiveDialog(string senderAlias, List<FileEntry> files, string fingerprint)
    {
        InitializeComponent();
        SenderText.Text = Loc.Tr("Receive.SenderWants", senderAlias, files.Count);

        foreach (var file in files)
        {
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = file.FileName, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = FormatSize(file.Size), FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(8, 0, 0, 0) });
            FileList.Items.Add(panel);
        }

        var totalSize = files.Sum(f => f.Size);
        TotalSizeText.Text = Loc.Tr("Receive.Total", FormatSize(totalSize));

        Tag = fingerprint;

        CountdownText.Text = Loc.Tr("Receive.Countdown", _countdown);
        _countdownTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _countdownTimer.Tick += OnCountdownTick;
        _countdownTimer.Start();
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _countdown--;
        if (_countdown <= 0)
        {
            _countdownTimer.Stop();
            Close();
            return;
        }
        CountdownText.Text = Loc.Tr("Receive.Countdown", _countdown);
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        _countdownTimer.Stop();
        Accepted = true;
        TrustDevice = TrustCheckBox.IsChecked == true;
        Close();
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        _countdownTimer.Stop();
        Accepted = false;
        Close();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:F1} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:F1} MB",
        >= 1_000 => $"{bytes / 1_000.0:F1} KB",
        _ => $"{bytes} B",
    };
}
