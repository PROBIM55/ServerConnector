using System.Windows;
using System.Text;
using Velopack;

namespace Connector.Desktop;

public partial class App : System.Windows.Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Package application is explicit and runs after the shared Agent drain.
        if (!System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "connector-install.json")))
            VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

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
