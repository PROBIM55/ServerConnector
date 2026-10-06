using System.Windows;
using System.Windows.Controls;

namespace Connector.Desktop.Features.Platform;

public partial class PlatformView : System.Windows.Controls.UserControl
{
    public PlatformView() => InitializeComponent();

    public void ClearToken()
    {
        var passwordBox = FindName("PlatformTokenPasswordBox") as System.Windows.Controls.PasswordBox;
        passwordBox?.Clear();
    }

    private void TokenChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is PlatformViewModel viewModel && sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            viewModel.Token = passwordBox.Password;
        }
    }
}
