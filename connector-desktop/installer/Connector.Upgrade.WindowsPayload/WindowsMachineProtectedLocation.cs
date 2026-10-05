namespace Connector.Upgrade.WindowsPayload;

/// <summary>
/// Shared machine-only directory boundary for installer journals and verified payloads. It creates
/// every missing component with its final protected ACL and rejects reparse points or untrusted
/// owners/permissions on existing components.
/// </summary>
public static class WindowsMachineProtectedLocation
{
    public static string PrepareDirectory(string absolutePath) =>
        new WindowsMachineStagingSecurity().PrepareRoot(absolutePath);

    public static void ValidateDirectory(string absolutePath) =>
        new WindowsMachineStagingSecurity().ValidateProtectedDirectory(absolutePath);
}
