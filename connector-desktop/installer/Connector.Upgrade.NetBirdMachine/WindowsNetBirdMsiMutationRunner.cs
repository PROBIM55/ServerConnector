using Connector.Upgrade.WindowsMsi;

namespace Connector.Upgrade.NetBirdMachine;

/// <summary>
/// Executes only fixed msiexec install/uninstall verbs. It performs no elevation and leaves every
/// outcome decision to the service's exact post-probe.
/// </summary>
public sealed class WindowsNetBirdMsiMutationRunner : INetBirdMsiMutationRunner
{
    private readonly IWindowsMsiProcessRunner _processRunner;
    private readonly string _msiexecPath;
    private readonly TimeSpan _timeout;

    public WindowsNetBirdMsiMutationRunner(
        IWindowsMsiProcessRunner? processRunner = null,
        TimeSpan? timeout = null,
        string? msiexecPath = null)
    {
        _processRunner = processRunner ?? new SystemWindowsMsiProcessRunner();
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        _msiexecPath = Path.GetFullPath(msiexecPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "msiexec.exe"));
    }

    public ValueTask<WindowsMsiProcessResult> InstallAsync(
        string protectedMsiPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(protectedMsiPath) || !Path.IsPathFullyQualified(protectedMsiPath))
            throw new ArgumentException("The protected MSI path must be absolute.", nameof(protectedMsiPath));
        return _processRunner.RunAsync(
            new WindowsMsiProcessRequest(
                _msiexecPath,
                ["/i", Path.GetFullPath(protectedMsiPath), "/qn", "/norestart"],
                _timeout),
            cancellationToken);
    }

    public ValueTask<WindowsMsiProcessResult> UninstallExactAsync(
        Guid productCode,
        CancellationToken cancellationToken)
    {
        if (productCode == Guid.Empty)
            throw new ArgumentException("An exact ProductCode is required.", nameof(productCode));
        return _processRunner.RunAsync(
            new WindowsMsiProcessRequest(
                _msiexecPath,
                ["/x", $"{{{productCode:D}}}".ToUpperInvariant(), "/qn", "/norestart"],
                _timeout),
            cancellationToken);
    }
}
