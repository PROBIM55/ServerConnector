using System.Security;
using System.Security.Cryptography;

namespace Connector.Upgrade.MachineServiceOrigin;

/// <summary>Fail-closed boundary for client-side machine pipe origin checks.</summary>
public static class MachineServiceOriginVerifier
{
    public const string ServiceName = "StructuraConnectorMachineService";
    public const string LocalSystemSid = "S-1-5-18";

    /// <summary>
    /// Client-side server-origin verification is currently unsupported. Windows documents
    /// GetNamedPipeServerProcessId as requiring a handle created by CreateNamedPipe, while a
    /// trusted client owns a CreateFile/client-end handle. This method always fails closed.
    /// </summary>
    public static void AssertTrustedServer(
        Microsoft.Win32.SafeHandles.SafePipeHandle connectedPipeHandle,
        string expectedExecutablePath,
        string? trustedSha256)
    {
        ArgumentNullException.ThrowIfNull(connectedPipeHandle);
        _ = expectedExecutablePath;
        _ = trustedSha256;
        throw new SecurityException(
            "Client-side machine pipe server-origin verification is unsupported; refusing to trust this connection.");
    }
}

/// <summary>Deterministic policy predicates retained for a future supported verification contract.</summary>
internal static class MachineServiceOriginPolicy
{
    public static bool IsMatchingRunningService(uint serviceType, uint currentState, uint servicePid, uint pipePid) =>
        serviceType == 0x00000010 && currentState == 4 && servicePid != 0 && servicePid == pipePid;

    public static bool IsLocalSystem(string? userSid) =>
        string.Equals(userSid, MachineServiceOriginVerifier.LocalSystemSid, StringComparison.Ordinal);

    public static bool PathsMatch(string actual, string expected) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), StringComparison.OrdinalIgnoreCase);

    public static bool HashesMatch(string expectedHex, string actualHex)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHex), Convert.FromHexString(actualHex));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
