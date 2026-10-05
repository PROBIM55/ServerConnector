using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;

namespace Connector.Upgrade.NetBirdWindowsState;

/// <summary>
/// Durable protected-machine restore store for a previously Connector-owned NetBird installation.
/// The implementation copies the Windows Installer cache, secret configuration and owner evidence;
/// it never invokes msiexec or reads configuration content into diagnostics.
/// </summary>
public sealed class WindowsNetBirdOwnedRestorePointStore : INetBirdOwnedRestorePointStore
{
    private readonly IWindowsNetBirdRestoreBackend _backend;

    public WindowsNetBirdOwnedRestorePointStore(string? machineRestoreRoot = null)
        : this(new SystemWindowsNetBirdRestoreBackend(machineRestoreRoot))
    {
    }

    internal WindowsNetBirdOwnedRestorePointStore(IWindowsNetBirdRestoreBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async ValueTask<IVerifiedNetBirdRestorePointLease> CaptureAsync(
        NetBirdMachineInspection exactOwnedInstallation,
        CancellationToken cancellationToken)
    {
        ValidateOwnedInspection(exactOwnedInstallation);
        var lease = await _backend.CaptureAsync(exactOwnedInstallation, cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateReturnedLease(lease, exactOwnedInstallation);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<IVerifiedNetBirdRestorePointLease> ReacquireAsync(
        NetBirdOwnedRestorePoint restorePoint,
        CancellationToken cancellationToken)
    {
        ValidateRestorePoint(restorePoint);
        var lease = await _backend.ReacquireAsync(restorePoint, cancellationToken).ConfigureAwait(false);
        try
        {
            if (lease.RestorePoint != restorePoint || lease.Package != restorePoint.Package ||
                !string.Equals(lease.Sha256, restorePoint.InstallerSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The reacquired NetBird restore lease differs from its durable receipt.");
            ValidateLeaseBasics(lease);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateOwnedInspection(NetBirdMachineInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        if (!inspection.InspectionComplete ||
            inspection.Assessment.Ownership != NetBirdOwnership.OwnedByConnector ||
            inspection.Assessment.OwnedState is null || inspection.InstalledPackage is null ||
            !inspection.ServiceIdentityVerified ||
            !WindowsNetBirdMachineStatePort.IsSha256(inspection.InstallerSha256) ||
            !WindowsNetBirdMachineStatePort.IsOperationId(inspection.OwnerOperationId) ||
            inspection.Assessment.OwnedState.InstallationId != inspection.Assessment.InstallationId ||
            !string.Equals(
                inspection.OwnerOperationId,
                inspection.Assessment.OwnedState.InstallationId,
                StringComparison.Ordinal))
            throw new NetBirdMachineInvariantException(
                "A restore point can be captured only from a complete exact Connector-owned NetBird inspection.");
    }

    private static void ValidateReturnedLease(
        IVerifiedNetBirdRestorePointLease lease,
        NetBirdMachineInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLeaseBasics(lease);
        if (lease is not IWindowsVerifiedNetBirdRestorePointLease windowsLease ||
            lease.Package != inspection.InstalledPackage ||
            lease.RestorePoint.OwnedState != inspection.Assessment.OwnedState ||
            !string.Equals(lease.Sha256, inspection.InstallerSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(windowsLease.PriorOwnerOperationId, inspection.OwnerOperationId, StringComparison.Ordinal))
            throw new InvalidDataException("The captured NetBird restore lease does not match the exact owned inspection.");
    }

    private static void ValidateLeaseBasics(IVerifiedNetBirdRestorePointLease lease)
    {
        if (lease.RestorePoint.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            lease.RestorePoint.Package != lease.Package || lease.SizeBytes <= 0 ||
            !string.Equals(lease.RestorePoint.InstallerSha256, lease.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !lease.AuthenticodeTrusted || !WindowsNetBirdMachineStatePort.IsSha256(lease.Sha256) ||
            string.IsNullOrWhiteSpace(lease.StagedPath) || string.IsNullOrWhiteSpace(lease.SignerSubject) ||
            string.IsNullOrWhiteSpace(lease.SignerThumbprint))
            throw new InvalidDataException("The NetBird restore lease is incomplete.");
    }

    private static void ValidateRestorePoint(NetBirdOwnedRestorePoint restorePoint)
    {
        ArgumentNullException.ThrowIfNull(restorePoint);
        if (restorePoint.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            !SystemWindowsNetBirdRestoreBackend.IsHandleId(restorePoint.HandleId) ||
            restorePoint.Package.ProductCode == Guid.Empty || restorePoint.Package.UpgradeCode == Guid.Empty ||
            restorePoint.Package.UpgradeCode != WindowsNetBirdMachineStatePort.OfficialUpgradeCode ||
            !string.Equals(
                restorePoint.Package.Manufacturer,
                WindowsNetBirdMachineStatePort.OfficialManufacturer,
                StringComparison.Ordinal) ||
            !string.Equals(
                restorePoint.Package.ProductName,
                WindowsNetBirdMachineStatePort.OfficialProductName,
                StringComparison.Ordinal) ||
            !WindowsNetBirdMachineStatePort.IsOperationId(restorePoint.OwnedState.InstallationId) ||
            !WindowsNetBirdMachineStatePort.IsSha256(restorePoint.OwnedState.ConfigurationSha256) ||
            !WindowsNetBirdMachineStatePort.IsSha256(restorePoint.InstallerSha256) ||
            !string.Equals(
                restorePoint.OwnedState.Version,
                restorePoint.Package.ProductVersion,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(restorePoint.OwnedState.ServiceIdentity))
            throw new NetBirdMachineInvariantException("The durable NetBird restore point is invalid.");
    }
}

internal interface IWindowsNetBirdRestoreBackend
{
    ValueTask<IWindowsVerifiedNetBirdRestorePointLease> CaptureAsync(
        NetBirdMachineInspection exactOwnedInstallation,
        CancellationToken cancellationToken);

    ValueTask<IWindowsVerifiedNetBirdRestorePointLease> ReacquireAsync(
        NetBirdOwnedRestorePoint restorePoint,
        CancellationToken cancellationToken);
}

internal sealed record WindowsNetBirdRestoreManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("handleId")] string HandleId,
    [property: JsonPropertyName("priorOwnerOperationId")] string PriorOwnerOperationId,
    [property: JsonPropertyName("installationId")] string InstallationId,
    [property: JsonPropertyName("productCode")] Guid ProductCode,
    [property: JsonPropertyName("upgradeCode")] Guid UpgradeCode,
    [property: JsonPropertyName("productVersion")] string ProductVersion,
    [property: JsonPropertyName("manufacturer")] string Manufacturer,
    [property: JsonPropertyName("productName")] string ProductName,
    [property: JsonPropertyName("installerSizeBytes")] long InstallerSizeBytes,
    [property: JsonPropertyName("installerSha256")] string InstallerSha256,
    [property: JsonPropertyName("signerSubject")] string SignerSubject,
    [property: JsonPropertyName("signerThumbprint")] string SignerThumbprint,
    [property: JsonPropertyName("configurationSizeBytes")] long ConfigurationSizeBytes,
    [property: JsonPropertyName("configurationSha256")] string ConfigurationSha256,
    [property: JsonPropertyName("serviceIdentity")] string ServiceIdentity,
    [property: JsonPropertyName("ownerMarkerSha256")] string OwnerMarkerSha256,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc)
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumBytes = 32 * 1024;

    internal NetBirdMsiPackageIdentity Package => new(
        ProductCode, UpgradeCode, ProductVersion, Manufacturer, ProductName);

    internal NetBirdOwnedState OwnedState => new(
        InstallationId, ProductVersion, ConfigurationSha256, ServiceIdentity);

    internal NetBirdOwnedRestorePoint RestorePoint => new(
        HandleId,
        OwnedState,
        RollbackPayloadProtection.ProtectedMachineStaging,
        Package,
        InstallerSha256);

    internal byte[] Serialize()
    {
        Validate();
        var content = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        if (content.Length > MaximumBytes)
            throw new InvalidDataException("The NetBird restore manifest exceeds its size limit.");
        return content;
    }

    internal static WindowsNetBirdRestoreManifest Parse(byte[] content)
    {
        if (content.Length is 0 or > MaximumBytes)
            throw new InvalidDataException("The NetBird restore manifest size is invalid.");
        var value = JsonSerializer.Deserialize<WindowsNetBirdRestoreManifest>(content, Options)
            ?? throw new InvalidDataException("The NetBird restore manifest is empty.");
        value.Validate();
        return value;
    }

    internal void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion ||
            !SystemWindowsNetBirdRestoreBackend.IsHandleId(HandleId) ||
            !WindowsNetBirdMachineStatePort.IsOperationId(PriorOwnerOperationId) ||
            !WindowsNetBirdMachineStatePort.IsOperationId(InstallationId) ||
            !string.Equals(PriorOwnerOperationId, InstallationId, StringComparison.Ordinal) ||
            ProductCode == Guid.Empty || UpgradeCode != WindowsNetBirdMachineStatePort.OfficialUpgradeCode ||
            string.IsNullOrWhiteSpace(ProductVersion) ||
            !string.Equals(Manufacturer, WindowsNetBirdMachineStatePort.OfficialManufacturer, StringComparison.Ordinal) ||
            !string.Equals(ProductName, WindowsNetBirdMachineStatePort.OfficialProductName, StringComparison.Ordinal) ||
            InstallerSizeBytes <= 0 || !WindowsNetBirdMachineStatePort.IsSha256(InstallerSha256) ||
            string.IsNullOrWhiteSpace(SignerSubject) || string.IsNullOrWhiteSpace(SignerThumbprint) ||
            ConfigurationSizeBytes <= 0 || !WindowsNetBirdMachineStatePort.IsSha256(ConfigurationSha256) ||
            string.IsNullOrWhiteSpace(ServiceIdentity) ||
            !WindowsNetBirdMachineStatePort.IsSha256(OwnerMarkerSha256) || CreatedUtc == default)
            throw new InvalidDataException("The NetBird restore manifest is invalid.");
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 8,
    };
}
