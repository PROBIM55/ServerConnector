using System.Security.Principal;
using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsJournal;
using Connector.Upgrade.WindowsPayload;

namespace Connector.Upgrade.WindowsHost;

/// <summary>
/// The recovery journal contains exact uninstall/rollback identities. Keep it beneath a machine
/// protected ProgramData directory so an unelevated user cannot forge a recovery receipt.
/// </summary>
public static class WindowsHostJournal
{
    public static WindowsProtectedUpgradeJournalStore OpenForCurrentUser(string initiatingUserSid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The protected upgrade journal requires Windows.");
        var expected = new SecurityIdentifier(initiatingUserSid ?? throw new ArgumentNullException(nameof(initiatingUserSid))).Value;
        using var identity = WindowsIdentity.GetCurrent();
        var current = identity.User?.Value ?? throw new UnauthorizedAccessException("The Windows token has no user SID.");
        if (!string.Equals(expected, current, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The elevated user is not the initiating user.");

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!Path.IsPathFullyQualified(programData))
            throw new InvalidDataException("ProgramData must resolve to an absolute directory.");
        var directory = Path.Combine(programData, "StructuraConnectorInstaller", "UpgradeJournal", expected);
        var protectedDirectory = WindowsMachineProtectedLocation.PrepareDirectory(directory);
        WindowsMachineProtectedLocation.ValidateDirectory(protectedDirectory);
        return new WindowsProtectedUpgradeJournalStore(protectedDirectory);
    }
}
