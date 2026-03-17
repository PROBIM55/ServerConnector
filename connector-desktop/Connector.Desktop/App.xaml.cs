using System.Windows;
using System.Text;

namespace Connector.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        base.OnStartup(e);

        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        MainWindow = window;
        window.Show();
        window.Activate();
    }
}
