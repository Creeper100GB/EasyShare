using System.Windows;
using EasyShare.Core.Models;

namespace EasyShare.App.Views;

public partial class ReceiveDialog : Wpf.Ui.Controls.FluentWindow
{
    public bool Accepted { get; private set; }
    public bool TrustDevice { get; private set; }

    public ReceiveDialog(string senderAlias, List<FileEntry> files, string fingerprint)
    {
        InitializeComponent();
        SenderText.Text = $"{senderAlias} möchte {files.Count} Datei(en) senden:";

        foreach (var file in files)
        {
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = file.FileName, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = FormatSize(file.Size), FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(8, 0, 0, 0) });
            FileList.Items.Add(panel);
        }

        var totalSize = files.Sum(f => f.Size);
        TotalSizeText.Text = $"Gesamt: {FormatSize(totalSize)}";

        Tag = fingerprint;
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        TrustDevice = TrustCheckBox.IsChecked == true;
        Close();
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
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
