using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;

namespace Connector.Upgrade.NetBirdWindowsState;

/// <summary>
/// Production Windows readback and ownership adapter for the official machine-wide NetBird MSI.
/// It never invokes NetBird or msiexec and never treats an existing unmarked installation as owned.
/// </summary>
public sealed class WindowsNetBirdMachineStatePort : INetBirdMachineStatePort
{
    internal static readonly Guid OfficialUpgradeCode = new("6456EC4E-3AD6-4B9B-A2BE-98E81CB21CCF");
    internal static readonly Guid OfficialPinnedProductCode = new("463D0C9D-ED41-451E-A44F-937932C8B267");
    internal const string OfficialManufacturer = "NetBird GmbH";
    internal const string OfficialProductName = "NetBird";

    private readonly IWindowsNetBirdStateEnvironment _environment;
    private readonly OfficialNetBirdPackagePin _pin;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InspectionEnvelope? _lastMutationFence;

    public WindowsNetBirdMachineStatePort()
        : this(new SystemWindowsNetBirdStateEnvironment(), OfficialNetBirdPackagePin.LoadEmbedded())
    {
    }

    internal WindowsNetBirdMachineStatePort(
        IWindowsNetBirdStateEnvironment environment,
        OfficialNetBirdPackagePin pin)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _pin = pin ?? throw new ArgumentNullException(nameof(pin));
    }

    public async ValueTask<NetBirdMachineInspection> InspectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = InspectCore();
            _lastMutationFence = result.Inspection.InspectionComplete &&
                result.Inspection.Assessment.Ownership is NetBirdOwnership.Absent or NetBirdOwnership.OwnedByConnector
                    ? result
                    : null;
            return result.Inspection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<NetBirdInstalledServiceVerification> VerifyInstalledPackageAndServiceAsync(
        NetBirdMsiPackageIdentity expectedPackage,
        CancellationToken cancellationToken)
    {
        ValidatePackage(expectedPackage);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _environment.Capture();
            var exact = snapshot.InventoryComplete &&
                snapshot.Service.QueryComplete && snapshot.Cli.QueryComplete &&
                snapshot.Registrations.Count == 1 &&
                snapshot.Registrations[0].Package == expectedPackage &&
                IsMachineRegistration(snapshot.Registrations[0]) &&
                IsOfficialPackage(snapshot.Registrations[0]) &&
                snapshot.Service.Present && snapshot.Service.IdentityExact &&
                snapshot.Cli.Present && snapshot.Cli.PathSecure &&
                IsPinnedCli(snapshot.Cli, expectedPackage.ProductVersion);
            return new NetBirdInstalledServiceVerification(
                exact,
                expectedPackage,
                exact ? snapshot.Service.ServiceIdentity : string.Empty,
                snapshot.EvidenceId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> VerifyPackageAbsentAsync(
        NetBirdMsiPackageIdentity expectedPackage,
        CancellationToken cancellationToken)
    {
        ValidatePackage(expectedPackage);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _environment.Capture();
            return snapshot.InventoryComplete &&
                snapshot.Registrations.All(value => value.Package.ProductCode != expectedPackage.ProductCode);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask PublishOwnershipAsync(
        string operationId,
        IVerifiedNetBirdPackageLease package,
        NetBirdInstalledServiceVerification service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(service);
        if (!IsOperationId(operationId))
            throw new NetBirdMachineInvariantException("The NetBird ownership operation id is invalid.");
        ValidateTargetLease(package);
        if (package.Package.ProductCode != OfficialPinnedProductCode ||
            package.Package.UpgradeCode != OfficialUpgradeCode ||
            !string.Equals(package.Package.ProductVersion, _pin.Version, StringComparison.Ordinal) ||
            !string.Equals(package.Package.Manufacturer, OfficialManufacturer, StringComparison.Ordinal) ||
            !string.Equals(package.Package.ProductName, OfficialProductName, StringComparison.Ordinal) ||
            !string.Equals(package.Sha256, _pin.InstallerSha256, StringComparison.OrdinalIgnoreCase) ||
            !package.SignerSubject.StartsWith(_pin.SignerSubjectPrefix, StringComparison.Ordinal) ||
            !string.Equals(package.SignerThumbprint, _pin.SignerThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new NetBirdMachineInvariantException(
                "Ownership publication accepts only the embedded official NetBird package pin.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fence = _lastMutationFence
                ?? throw new NetBirdMachineInvariantException(
                    "Ownership publication requires a complete absent or Connector-owned pre-mutation inspection.");
            var current = _environment.Capture();
            RequireExactCurrentForPublication(current, package.Package, service);
            RequireCausalMarkerFence(fence, current);

            var marker = new WindowsNetBirdOwnerMarker(
                WindowsNetBirdOwnerMarker.CurrentSchemaVersion,
                WindowsNetBirdOwnerMarker.ExpectedProvisioner,
                operationId,
                operationId,
                package.Package.ProductCode,
                package.Package.UpgradeCode,
                package.Package.ProductVersion,
                package.Package.Manufacturer,
                package.Package.ProductName,
                package.Sha256.ToUpperInvariant(),
                current.Configuration.Sha256!,
                service.ServiceIdentity,
                current.Cli.Sha256,
                current.Cli.SignerSubject,
                current.Cli.SignerThumbprint,
                DateTimeOffset.UtcNow);
            _environment.WriteOwnerMarker(marker.Serialize());

            var after = InspectCore();
            if (!after.Inspection.InspectionComplete ||
                after.Inspection.Assessment.Ownership != NetBirdOwnership.OwnedByConnector ||
                after.Inspection.Assessment.OwnedState != marker.OwnedState ||
                !string.Equals(after.Inspection.OwnerOperationId, operationId, StringComparison.Ordinal))
                throw new NetBirdManualRecoveryRequiredException(
                    "The protected NetBird owner marker was not proven after publication.");
            _lastMutationFence = after;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RemoveOwnershipAsync(
        NetBirdOwnedState exactState,
        CancellationToken cancellationToken)
    {
        ValidateOwnedState(exactState);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _environment.Capture();
            RequireProtectedFilesReadable(snapshot);
            var marker = ParseRequiredMarker(snapshot.OwnerMarker);
            if (marker.OwnedState != exactState)
                throw new NetBirdMachineInvariantException(
                    "The protected NetBird owner marker does not match the exact state being removed.");
            if (!snapshot.InventoryComplete || snapshot.Registrations.Count != 0 ||
                snapshot.Service.Present || snapshot.Cli.Present)
                throw new NetBirdMachineInvariantException(
                    "NetBird ownership cannot be removed while package, service, or CLI artifacts remain.");

            _environment.DeleteOwnerMarker();
            var after = _environment.Capture();
            if (after.OwnerMarker.State != NetBirdProtectedFileState.Missing)
                throw new NetBirdManualRecoveryRequiredException("The NetBird owner marker removal was not proven.");
            _lastMutationFence = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RestoreOwnershipAsync(
        IVerifiedNetBirdRestorePointLease restorePoint,
        NetBirdInstalledServiceVerification service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(restorePoint);
        ArgumentNullException.ThrowIfNull(service);
        if (restorePoint is not IWindowsVerifiedNetBirdRestorePointLease windowsLease)
            throw new NetBirdMachineInvariantException(
                "The NetBird restore lease does not contain protected Windows configuration evidence.");
        ValidateRestoreLease(windowsLease);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = _environment.Capture();
            RequireExactCurrentForRestore(before, windowsLease.Package, service);
            _ = ParseRequiredMarker(before.OwnerMarker);
            _environment.RestoreConfiguration(
                windowsLease.ProtectedConfigurationPath,
                windowsLease.RestorePoint.OwnedState.ConfigurationSha256);
            var restored = _environment.Capture();
            if (restored.Configuration.State != NetBirdProtectedFileState.Present ||
                !string.Equals(
                    restored.Configuration.Sha256,
                    windowsLease.RestorePoint.OwnedState.ConfigurationSha256,
                    StringComparison.Ordinal))
                throw new NetBirdManualRecoveryRequiredException(
                    "The protected prior NetBird configuration was not restored exactly.");

            var marker = new WindowsNetBirdOwnerMarker(
                WindowsNetBirdOwnerMarker.CurrentSchemaVersion,
                WindowsNetBirdOwnerMarker.ExpectedProvisioner,
                windowsLease.PriorOwnerOperationId,
                windowsLease.RestorePoint.OwnedState.InstallationId,
                windowsLease.Package.ProductCode,
                windowsLease.Package.UpgradeCode,
                windowsLease.Package.ProductVersion,
                windowsLease.Package.Manufacturer,
                windowsLease.Package.ProductName,
                windowsLease.Sha256.ToUpperInvariant(),
                windowsLease.RestorePoint.OwnedState.ConfigurationSha256,
                service.ServiceIdentity,
                restored.Cli.Sha256,
                restored.Cli.SignerSubject,
                restored.Cli.SignerThumbprint,
                DateTimeOffset.UtcNow);
            _environment.WriteOwnerMarker(marker.Serialize());
            var after = InspectCore();
            if (!after.Inspection.InspectionComplete ||
                after.Inspection.Assessment.Ownership != NetBirdOwnership.OwnedByConnector ||
                after.Inspection.Assessment.OwnedState != windowsLease.RestorePoint.OwnedState)
                throw new NetBirdManualRecoveryRequiredException(
                    "The exact prior Connector-owned NetBird state was not proven after restoration.");
            _lastMutationFence = after;
        }
        finally
        {
            _gate.Release();
        }
    }

    private InspectionEnvelope InspectCore()
    {
        var snapshot = _environment.Capture();
        var inspection = Classify(snapshot);
        return new InspectionEnvelope(snapshot, inspection);
    }

    private NetBirdMachineInspection Classify(WindowsNetBirdSnapshot snapshot)
    {
        var protectedFilesComplete = snapshot.OwnerMarker.State is not (
                NetBirdProtectedFileState.Unsafe or NetBirdProtectedFileState.Unreadable) &&
            snapshot.Configuration.State is not (
                NetBirdProtectedFileState.Unsafe or NetBirdProtectedFileState.Unreadable);
        var complete = snapshot.InventoryComplete && snapshot.Service.QueryComplete &&
            snapshot.Cli.QueryComplete && protectedFilesComplete &&
            (!snapshot.Cli.Present || snapshot.Cli.PathSecure);
        if (!complete)
            return Incomplete(snapshot.EvidenceId);

        var hasArtifacts = snapshot.Registrations.Count != 0 || snapshot.Service.Present ||
            snapshot.Cli.Present || snapshot.OwnerMarker.State == NetBirdProtectedFileState.Present ||
            snapshot.Configuration.State == NetBirdProtectedFileState.Present;
        if (!hasArtifacts)
            return new NetBirdMachineInspection(
                true,
                new NetBirdAssessment(NetBirdOwnership.Absent, null),
                null,
                null,
                false,
                snapshot.EvidenceId);

        if (snapshot.Registrations.Count != 1)
            return Unattributed(snapshot, NetBirdOwnership.Foreign);
        var registration = snapshot.Registrations[0];
        if (!IsMachineRegistration(registration) || !IsOfficialPackage(registration))
            return Unattributed(snapshot, NetBirdOwnership.Foreign);
        if (!snapshot.Service.Present || !snapshot.Service.IdentityExact ||
            !snapshot.Cli.Present || !IsGenerallyTrustedCli(snapshot.Cli, registration.Package.ProductVersion) ||
            snapshot.Configuration.State != NetBirdProtectedFileState.Present)
            return Unattributed(snapshot, NetBirdOwnership.Unattributed);
        if (snapshot.OwnerMarker.State != NetBirdProtectedFileState.Present)
            return Unattributed(snapshot, NetBirdOwnership.Foreign);

        WindowsNetBirdOwnerMarker marker;
        try
        {
            marker = ParseRequiredMarker(snapshot.OwnerMarker);
        }
        catch (InvalidDataException)
        {
            return Unattributed(snapshot, NetBirdOwnership.Unattributed);
        }

        if (marker.Package != registration.Package ||
            !string.Equals(marker.ConfigurationSha256, snapshot.Configuration.Sha256, StringComparison.Ordinal) ||
            !string.Equals(marker.ServiceIdentity, snapshot.Service.ServiceIdentity, StringComparison.Ordinal) ||
            !string.Equals(marker.CliSha256, snapshot.Cli.Sha256, StringComparison.Ordinal) ||
            !string.Equals(marker.CliSignerSubject, snapshot.Cli.SignerSubject, StringComparison.Ordinal) ||
            !string.Equals(
                marker.CliSignerThumbprint,
                snapshot.Cli.SignerThumbprint,
                StringComparison.OrdinalIgnoreCase))
            return Unattributed(snapshot, NetBirdOwnership.Unattributed);

        return new NetBirdMachineInspection(
            true,
            new NetBirdAssessment(NetBirdOwnership.OwnedByConnector, marker.InstallationId, marker.OwnedState),
            registration.Package,
            marker.InstallerSha256,
            true,
            snapshot.EvidenceId,
            marker.OperationId);
    }

    private static NetBirdMachineInspection Incomplete(string evidenceId) => new(
        false,
        new NetBirdAssessment(NetBirdOwnership.Unattributed, null),
        null,
        null,
        false,
        evidenceId);

    private static NetBirdMachineInspection Unattributed(
        WindowsNetBirdSnapshot snapshot,
        NetBirdOwnership ownership)
    {
        var package = snapshot.Registrations.Count == 1 ? snapshot.Registrations[0].Package : null;
        return new NetBirdMachineInspection(
            true,
            new NetBirdAssessment(ownership, package?.ProductCode.ToString("D")),
            package,
            null,
            false,
            snapshot.EvidenceId);
    }

    private void RequireExactCurrentForPublication(
        WindowsNetBirdSnapshot snapshot,
        NetBirdMsiPackageIdentity package,
        NetBirdInstalledServiceVerification service)
    {
        if (!snapshot.InventoryComplete || !snapshot.Service.QueryComplete || !snapshot.Cli.QueryComplete ||
            snapshot.Registrations.Count != 1 || snapshot.Registrations[0].Package != package ||
            !IsMachineRegistration(snapshot.Registrations[0]) || !IsOfficialPackage(snapshot.Registrations[0]) ||
            !snapshot.Service.Present || !snapshot.Service.IdentityExact ||
            !snapshot.Cli.Present || !snapshot.Cli.PathSecure || !IsPinnedCli(snapshot.Cli, package.ProductVersion) ||
            snapshot.Configuration.State != NetBirdProtectedFileState.Present ||
            !IsSha256(snapshot.Configuration.Sha256) ||
            !service.IsExact || service.Package != package ||
            !string.Equals(service.ServiceIdentity, snapshot.Service.ServiceIdentity, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(service.EvidenceId))
            throw new NetBirdMachineInvariantException(
                "Ownership publication requires exact official MSI, service, signed CLI, and protected configuration evidence.");
    }

    private static void RequireCausalMarkerFence(InspectionEnvelope fence, WindowsNetBirdSnapshot current)
    {
        if (fence.Inspection.Assessment.Ownership == NetBirdOwnership.Absent)
        {
            if (current.OwnerMarker.State != NetBirdProtectedFileState.Missing)
                throw new NetBirdMachineInvariantException(
                    "An owner marker appeared after the absent pre-mutation fence.");
            return;
        }

        if (fence.Snapshot.OwnerMarker.State != NetBirdProtectedFileState.Present ||
            current.OwnerMarker.State != NetBirdProtectedFileState.Present ||
            fence.Snapshot.OwnerMarker.Content is null || current.OwnerMarker.Content is null ||
            !fence.Snapshot.OwnerMarker.Content.AsSpan().SequenceEqual(current.OwnerMarker.Content))
            throw new NetBirdMachineInvariantException(
                "The prior Connector owner marker changed during the NetBird mutation.");
    }

    private void RequireExactCurrentForRestore(
        WindowsNetBirdSnapshot snapshot,
        NetBirdMsiPackageIdentity package,
        NetBirdInstalledServiceVerification service)
    {
        if (!snapshot.InventoryComplete || !snapshot.Service.QueryComplete || !snapshot.Cli.QueryComplete ||
            snapshot.Registrations.Count != 1 || snapshot.Registrations[0].Package != package ||
            !IsMachineRegistration(snapshot.Registrations[0]) || !IsOfficialPackage(snapshot.Registrations[0]) ||
            !snapshot.Service.Present || !snapshot.Service.IdentityExact ||
            !snapshot.Cli.Present || !snapshot.Cli.PathSecure ||
            !IsGenerallyTrustedCli(snapshot.Cli, package.ProductVersion) ||
            snapshot.Configuration.State != NetBirdProtectedFileState.Present ||
            !IsSha256(snapshot.Configuration.Sha256) ||
            !service.IsExact || service.Package != package ||
            !string.Equals(service.ServiceIdentity, snapshot.Service.ServiceIdentity, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(service.EvidenceId) ||
            snapshot.OwnerMarker.State != NetBirdProtectedFileState.Present)
            throw new NetBirdMachineInvariantException(
                "Prior ownership restoration requires exact official MSI, service, signed CLI, configuration, and current owner evidence.");
    }

    private bool IsPinnedCli(WindowsNetBirdCliSnapshot cli, string packageVersion) =>
        IsGenerallyTrustedCli(cli, packageVersion) &&
        cli.SignerSubject.StartsWith(_pin.SignerSubjectPrefix, StringComparison.Ordinal) &&
        string.Equals(cli.SignerThumbprint, _pin.SignerThumbprint, StringComparison.OrdinalIgnoreCase);

    private static bool IsGenerallyTrustedCli(WindowsNetBirdCliSnapshot cli, string packageVersion) =>
        cli.AuthenticodeTrusted && IsSha256(cli.Sha256) &&
        cli.SignerSubject.StartsWith("CN=NetBird GmbH,", StringComparison.Ordinal) &&
        cli.SignerThumbprint.Length == 40 && cli.SignerThumbprint.All(Uri.IsHexDigit) &&
        VersionMatches(cli.Version, packageVersion);

    private static bool VersionMatches(string actual, string expected)
    {
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            return true;
        return actual.StartsWith(expected + ".", StringComparison.OrdinalIgnoreCase) &&
            actual[(expected.Length + 1)..].All(char.IsDigit);
    }

    private bool IsOfficialPackage(WindowsNetBirdRegistration registration) =>
        registration.Package.UpgradeCode == OfficialUpgradeCode &&
        string.Equals(registration.Package.Manufacturer, OfficialManufacturer, StringComparison.Ordinal) &&
        string.Equals(registration.Package.ProductName, OfficialProductName, StringComparison.Ordinal) &&
        Version.TryParse(registration.Package.ProductVersion, out var installed) &&
        Version.TryParse(_pin.MinimumSecureVersion, out var minimum) && installed >= minimum;

    private static bool IsMachineRegistration(WindowsNetBirdRegistration registration) =>
        string.Equals(registration.RegistrationContext, "Machine", StringComparison.Ordinal) &&
        string.IsNullOrWhiteSpace(registration.UserSid);

    private static WindowsNetBirdOwnerMarker ParseRequiredMarker(WindowsNetBirdProtectedFileSnapshot snapshot)
    {
        if (snapshot.State != NetBirdProtectedFileState.Present || snapshot.Content is null)
            throw new InvalidDataException("The protected NetBird owner marker is missing.");
        return WindowsNetBirdOwnerMarker.Parse(snapshot.Content);
    }

    private static void RequireProtectedFilesReadable(WindowsNetBirdSnapshot snapshot)
    {
        if (snapshot.OwnerMarker.State is NetBirdProtectedFileState.Unsafe or NetBirdProtectedFileState.Unreadable ||
            snapshot.Configuration.State is NetBirdProtectedFileState.Unsafe or NetBirdProtectedFileState.Unreadable)
            throw new NetBirdMachineInvariantException("Protected NetBird state is unsafe or unreadable.");
    }

    private static void ValidateTargetLease(IVerifiedNetBirdPackageLease package)
    {
        ValidatePackage(package.Package);
        if (package.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            !package.AuthenticodeTrusted || package.SizeBytes <= 0 || !IsSha256(package.Sha256) ||
            string.IsNullOrWhiteSpace(package.HandleId) || string.IsNullOrWhiteSpace(package.StagedPath) ||
            string.IsNullOrWhiteSpace(package.SignerSubject) || string.IsNullOrWhiteSpace(package.SignerThumbprint))
            throw new NetBirdMachineInvariantException("The NetBird package lease is not protected and verified.");
    }

    private static void ValidateRestoreLease(IWindowsVerifiedNetBirdRestorePointLease lease)
    {
        ValidatePackage(lease.Package);
        ValidateOwnedState(lease.RestorePoint.OwnedState);
        if (lease.RestorePoint.Package != lease.Package ||
            lease.RestorePoint.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            string.IsNullOrWhiteSpace(lease.RestorePoint.HandleId) ||
            !string.Equals(lease.RestorePoint.InstallerSha256, lease.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !IsSha256(lease.Sha256) || lease.SizeBytes <= 0 || !lease.AuthenticodeTrusted ||
            string.IsNullOrWhiteSpace(lease.ProtectedConfigurationPath) ||
            !IsOperationId(lease.PriorOwnerOperationId) ||
            !string.Equals(
                lease.PriorOwnerOperationId,
                lease.RestorePoint.OwnedState.InstallationId,
                StringComparison.Ordinal))
            throw new NetBirdMachineInvariantException("The NetBird restore lease is incomplete or inconsistent.");
    }

    private static void ValidatePackage(NetBirdMsiPackageIdentity package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.ProductCode == Guid.Empty || package.UpgradeCode == Guid.Empty ||
            string.IsNullOrWhiteSpace(package.ProductVersion) ||
            string.IsNullOrWhiteSpace(package.Manufacturer) || string.IsNullOrWhiteSpace(package.ProductName))
            throw new NetBirdMachineInvariantException("The NetBird MSI package identity is incomplete.");
    }

    private static void ValidateOwnedState(NetBirdOwnedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsOperationId(state.InstallationId) || string.IsNullOrWhiteSpace(state.Version) ||
            !IsSha256(state.ConfigurationSha256) || string.IsNullOrWhiteSpace(state.ServiceIdentity))
            throw new NetBirdMachineInvariantException("The NetBird owned state is incomplete.");
    }

    internal static bool IsSha256(string? value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    internal static bool IsOperationId(string? value) =>
        value?.Length == 32 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record InspectionEnvelope(
        WindowsNetBirdSnapshot Snapshot,
        NetBirdMachineInspection Inspection);
}
