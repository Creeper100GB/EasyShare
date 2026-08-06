using System.Windows;
using EasyShare.App.Localization;

namespace EasyShare.App.Views;

public partial class CrashDialog : Wpf.Ui.Controls.FluentWindow
{
    public CrashDialog(Exception exception)
    {
        InitializeComponent();
        var details = $"{exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{Environment.NewLine}{exception.StackTrace}";
        DetailsBox.Text = $"{Loc.Tr("App.CrashIntro")}{Environment.NewLine}{Environment.NewLine}{details}";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(DetailsBox.Text);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
