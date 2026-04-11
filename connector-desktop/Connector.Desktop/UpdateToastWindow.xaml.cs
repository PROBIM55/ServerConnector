using System.Windows;

namespace Connector.Desktop;

public partial class UpdateToastWindow : Window
{
    private readonly Func<Task> _installAsync;
    private bool _installStarted;

    public UpdateToastWindow(string title, string message, Func<Task> installAsync)
    {
        InitializeComponent();
        _installAsync = installAsync;
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 18;
        Top = area.Bottom - Height - 18;
    }

    public void BringToFront()
    {
        if (!IsVisible)
        {
            Show();
        }

        Topmost = true;
        Topmost = false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installStarted)
        {
            return;
        }

        _installStarted = true;
        try
        {
            await _installAsync();
            Close();
        }
        finally
        {
            _installStarted = false;
        }
    }
}
