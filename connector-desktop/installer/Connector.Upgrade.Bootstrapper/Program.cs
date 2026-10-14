namespace Connector.Upgrade.Bootstrapper;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        // Deliberately no command-line or environment-variable input is consumed.
        using var form = new StandaloneInstallForm(new ProductionStandaloneDesktopInstaller());
        Application.Run(form);
        return form.ExitCode;
    }
}
