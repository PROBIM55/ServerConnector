using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;

namespace Connector.Upgrade.NetBirdWindowsState;

internal enum NetBirdProtectedFileState
{
    Missing = 0,
    Present = 1,
    Unsafe = 2,
    Unreadable = 3,
}

internal sealed record WindowsNetBirdRegistration(
    NetBirdMsiPackageIdentity Package,
    string RegistrationContext,
    string? UserSid,
    string? LocalPackagePath);

internal sealed record WindowsNetBirdServiceSnapshot(
    bool QueryComplete,
    bool Present,
    string Name,
    string ImagePath,
    int StartType,
    string AccountName,
    bool IdentityExact,
    string ServiceIdentity,
    int ServiceType = 16,
    int ErrorControl = 1);

internal sealed record WindowsNetBirdCliSnapshot(
    bool QueryComplete,
    bool Present,
    string Path,
    string Version,
    string Sha256,
    bool PathSecure,
    bool AuthenticodeTrusted,
    string SignerSubject,
    string SignerThumbprint);

internal sealed record WindowsNetBirdProtectedFileSnapshot(
    NetBirdProtectedFileState State,
    byte[]? Content = null,
    string? Sha256 = null);

internal sealed record WindowsNetBirdSnapshot(
    bool InventoryComplete,
    IReadOnlyList<WindowsNetBirdRegistration> Registrations,
    WindowsNetBirdServiceSnapshot Service,
    WindowsNetBirdCliSnapshot Cli,
    WindowsNetBirdProtectedFileSnapshot OwnerMarker,
    WindowsNetBirdProtectedFileSnapshot Configuration,
    string EvidenceId);

internal interface IWindowsNetBirdStateEnvironment
{
    string ConfigurationPath { get; }
    WindowsNetBirdSnapshot Capture();
    void WriteOwnerMarker(byte[] content);
    void DeleteOwnerMarker();
    void RestoreConfiguration(string protectedSourcePath, string expectedSha256);
}

/// <summary>
/// Restore leases used by the Windows state port carry the protected prior owner marker and
/// configuration in addition to the public MSI receipt. Other lease implementations are rejected.
/// </summary>
public interface IWindowsVerifiedNetBirdRestorePointLease : IVerifiedNetBirdRestorePointLease
{
    string ProtectedConfigurationPath { get; }
    string PriorOwnerOperationId { get; }
}
