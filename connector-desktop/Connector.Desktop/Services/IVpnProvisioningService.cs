namespace Connector.Desktop.Services;

public interface IVpnProvisioningService
{
    bool BundledClientPresent { get; }
    string LogFilePath { get; }
    bool IsTunnelInstalled(string tunnel);
    bool IsTunnelRunning(string tunnel);
    VpnResult Enable(string configContent, string tunnel, bool restartExisting = false);
    VpnResult Disable(string tunnel);
    Task<bool> CanReachServerAsync(string serverIp);
}
