using System.Security.Principal;

namespace Connector.Upgrade.NetBirdMachine;

/// <summary>Checks the existing token only. It never starts UAC or another process.</summary>
public sealed class WindowsTrustedElevatedCallerGate : ITrustedElevatedCallerGate
{
    private static readonly HashSet<string> TrustedServiceSids = new(StringComparer.Ordinal)
    {
        "S-1-5-18", // LocalSystem
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // TrustedInstaller
    };

    public void AssertTrustedElevatedCaller()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("NetBird machine mutation is available only on Windows.");

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (identity.User is not null && TrustedServiceSids.Contains(identity.User.Value))
            return;
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException(
                "A caller that is already elevated as Administrator, SYSTEM, or TrustedInstaller is required.");
    }
}
