namespace Connector.Upgrade.MachineHelper;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var pins = new Connector.Upgrade.ProtectedCallerImage.ProtectedCallerImagePinSource();
            var runtime = new MachineHelperRuntime(
                WindowsHelperCallerProcessOpener.Instance,
                new WindowsHelperPreSessionTrust(),
                new MutualMachineHelperCommandSessionHost(pins),
                new WindowsMachineHelperComposition());
            await runtime.RunAsync(args).ConfigureAwait(false);
            return 0;
        }
        catch
        {
            // The helper is intentionally silent: diagnostics must not disclose machine state or paths.
            return 1;
        }
    }
}
