using System.Windows;
using System.Text;
using Connector.Desktop.Services;
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

        using var singleInstance = SingleInstanceCoordinator.AcquireOrSignal();
        if (!singleInstance.IsPrimary)
        {
            return;
        }

        var app = new App();
        app._singleInstance = singleInstance;
        app.InitializeComponent();
        app.Run();
    }

    private SingleInstanceCoordinator? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        base.OnStartup(e);

        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        MainWindow = window;
        _singleInstance?.SetActivationHandler(() => Dispatcher.BeginInvoke(new Action(() => Activate(window))));
        window.Show();
        window.Activate();
    }

    private static void Activate(Window window)
    {
        window.ShowInTaskbar = true;
        if (!window.IsVisible) window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
