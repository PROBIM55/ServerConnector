using Microsoft.Win32.SafeHandles;
using Connector.Upgrade.Velopack;

namespace Connector.Upgrade.VelopackPayload;

public sealed record VelopackSetupSource(string SetupPath);

/// <summary>Fail-closed trust policy for the Authenticode certificate used by Setup.exe.</summary>
public sealed record VelopackSetupSignaturePolicy(IReadOnlySet<string> AllowedSignerThumbprints)
{
    public void Validate()
    {
        if (AllowedSignerThumbprints is null || AllowedSignerThumbprints.Count == 0 ||
            AllowedSignerThumbprints.Any(static value => string.IsNullOrWhiteSpace(value)))
            throw new InvalidDataException("At least one pinned Authenticode signer thumbprint is required.");
    }
}

public sealed record NtfsFileIdentity(uint VolumeSerialNumber, ulong FileIndex);

public sealed record ProtectedVelopackSetupReceipt(
    string HandleId,
    string StagedPath,
    VelopackSetupInspection Inspection,
    NtfsFileIdentity FileIdentity);

public interface IWindowsVerifiedVelopackSetupLease : IVerifiedVelopackSetupLease
{
    NtfsFileIdentity FileIdentity { get; }
}

public interface IWindowsVelopackSetupStager
{
    ValueTask<IVerifiedVelopackSetupLease> StageAndVerifyAsync(CancellationToken cancellationToken = default);
    /// <summary>Reopens and re-verifies a protected slot addressed only by its opaque durable handle.</summary>
    ValueTask<IVerifiedVelopackSetupLease> ReacquireByHandleAsync(
        string handleId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IVerifiedVelopackSetupLease>(
            new NotSupportedException("This stager cannot reacquire a protected slot by opaque handle."));
    ValueTask<IVerifiedVelopackSetupLease> ReacquireAsync(
        ProtectedVelopackSetupReceipt receipt,
        CancellationToken cancellationToken = default);
}

public static class WindowsVelopackSetupStagerFactory
{
    private static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "StructuraConnectorInstaller", "VelopackSetup");

    public static IWindowsVelopackSetupStager Create(
        VelopackSetupSource source,
        VelopackSetupPin pin,
        VelopackSetupSignaturePolicy? signaturePolicy,
        string initiatingUserSid,
        string? machineStagingRoot = null) => new WindowsVelopackSetupStager(
            source,
            pin,
            signaturePolicy,
            initiatingUserSid,
            machineStagingRoot ?? DefaultRoot);

    /// <summary>
    /// Reopens a machine-staged, exact signed Setup for the initiating user without creating or
    /// modifying machine files. The caller must obtain pin and signer policy from the verified
    /// release manifest, after a successful authenticated StageVelopack command.
    /// </summary>
    public static IWindowsVelopackSetupStager OpenExistingForOriginalUser(
        VelopackSetupPin pin,
        VelopackSetupSignaturePolicy? signaturePolicy,
        string initiatingUserSid,
        string? machineStagingRoot = null) => new WindowsVelopackSetupStager(
            new VelopackSetupSource(string.Empty), pin, signaturePolicy, initiatingUserSid,
            machineStagingRoot ?? DefaultRoot, hooks: null, readOnlyExisting: true);
}
