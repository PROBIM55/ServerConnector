using System.Windows;
using System.Windows.Input;

namespace Connector.Desktop;

public partial class ThemedDialogWindow : Window
{
    public MessageBoxResult DialogResultValue { get; private set; } = MessageBoxResult.None;

    public ThemedDialogWindow(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        TitleTextBlock.Text = string.IsNullOrWhiteSpace(title) ? "Уведомление" : title;
        MessageTextBlock.Text = message ?? string.Empty;

        switch (buttons)
        {
            case MessageBoxButton.YesNo:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                OkButton.Visibility = Visibility.Collapsed;
                YesButton.Focus();
                break;
            default:
                OkButton.Visibility = Visibility.Visible;
                OkButton.Focus();
                break;
        }

        switch (image)
        {
            case MessageBoxImage.Warning:
                IconTextBlock.Text = "⚠";
                IconTextBlock.Foreground = System.Windows.Media.Brushes.Goldenrod;
                break;
            case MessageBoxImage.Error:
                IconTextBlock.Text = "⛔";
                IconTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                break;
            case MessageBoxImage.Question:
                IconTextBlock.Text = "?";
                IconTextBlock.Foreground = System.Windows.Media.Brushes.LightSkyBlue;
                break;
            default:
                IconTextBlock.Text = "ℹ";
                IconTextBlock.Foreground = System.Windows.Media.Brushes.LightSkyBlue;
                break;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResultValue = MessageBoxResult.Cancel;
        Close();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResultValue = MessageBoxResult.OK;
        Close();
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResultValue = MessageBoxResult.Yes;
        Close();
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResultValue = MessageBoxResult.No;
        Close();
    }
}

public static class ThemedDialogs
{
    public static MessageBoxResult Show(Window owner, string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var dialog = new ThemedDialogWindow(message, title, buttons, image)
        {
            Owner = owner
        };
        dialog.ShowDialog();
        return dialog.DialogResultValue;
    }
}
