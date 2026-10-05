using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;

namespace Connector.Upgrade.NetBirdWindowsState.Tests;

internal static class WindowsNetBirdStateTestSupport
{
    internal static readonly OfficialNetBirdPackagePin Pin = OfficialNetBirdPackagePin.LoadEmbedded();
    internal static readonly NetBirdMsiPackageIdentity Package = new(
        WindowsNetBirdMachineStatePort.OfficialPinnedProductCode,
        WindowsNetBirdMachineStatePort.OfficialUpgradeCode,
        Pin.Version,
        WindowsNetBirdMachineStatePort.OfficialManufacturer,
        WindowsNetBirdMachineStatePort.OfficialProductName);
    internal static readonly string ConfigurationHash = new('A', 64);
    internal static readonly string CliHash = new('B', 64);
    internal static readonly string InstallerHash = Pin.InstallerSha256;
    internal static readonly string PriorOperationId = "22222222222222222222222222222222";
    internal static readonly string InstallationId = PriorOperationId;
    internal static readonly string NewOperationId = "33333333333333333333333333333333";
    internal static readonly string ServiceIdentity = "netbird-service-v1:" + new string('C', 64);

    internal static WindowsNetBirdSnapshot Absent(string evidence = "absent") => new(
        true,
        [],
        MissingService(),
        MissingCli(),
        MissingFile(),
        MissingFile(),
        evidence);

    internal static WindowsNetBirdSnapshot Exact(
        WindowsNetBirdOwnerMarker? marker,
        string evidence = "exact",
        bool serviceExact = true,
        bool inventoryComplete = true,
        NetBirdProtectedFileState markerState = NetBirdProtectedFileState.Present) => new(
        inventoryComplete,
        [new WindowsNetBirdRegistration(Package, "Machine", null, @"C:\Windows\Installer\netbird.msi")],
        new WindowsNetBirdServiceSnapshot(
            true, true, "NetBird", "image", 2, "LocalSystem", serviceExact, ServiceIdentity),
        new WindowsNetBirdCliSnapshot(
            true, true, @"C:\Program Files\Netbird\netbird.exe", Pin.Version, CliHash,
            true, true, "CN=NetBird GmbH, O=NetBird GmbH", Pin.SignerThumbprint),
        markerState switch
        {
            NetBirdProtectedFileState.Present when marker is not null =>
                new WindowsNetBirdProtectedFileSnapshot(markerState, marker.Serialize(), null),
            _ => new WindowsNetBirdProtectedFileSnapshot(markerState),
        },
        new WindowsNetBirdProtectedFileSnapshot(
            NetBirdProtectedFileState.Present, null, ConfigurationHash),
        evidence);

    internal static WindowsNetBirdOwnerMarker Marker(
        string operationId = "22222222222222222222222222222222",
        string installationId = "22222222222222222222222222222222",
        NetBirdMsiPackageIdentity? package = null,
        string? serviceIdentity = null,
        string? configurationHash = null,
        string? installerHash = null,
        string? cliSignerSubject = null,
        string? cliSignerThumbprint = null) => new(
        WindowsNetBirdOwnerMarker.CurrentSchemaVersion,
        WindowsNetBirdOwnerMarker.ExpectedProvisioner,
        operationId,
        installationId,
        (package ?? Package).ProductCode,
        (package ?? Package).UpgradeCode,
        (package ?? Package).ProductVersion,
        (package ?? Package).Manufacturer,
        (package ?? Package).ProductName,
        installerHash ?? InstallerHash,
        configurationHash ?? ConfigurationHash,
        serviceIdentity ?? ServiceIdentity,
        CliHash,
        cliSignerSubject ?? "CN=NetBird GmbH, O=NetBird GmbH",
        cliSignerThumbprint ?? Pin.SignerThumbprint,
        DateTimeOffset.Parse("2026-10-02T00:00:00Z"));

    internal static NetBirdInstalledServiceVerification ServiceVerification() => new(
        true,
        Package,
        ServiceIdentity,
        "verify");

    internal static WindowsNetBirdServiceSnapshot MissingService() => new(
        true, false, "NetBird", string.Empty, -1, string.Empty, false, string.Empty);

    internal static WindowsNetBirdCliSnapshot MissingCli() => new(
        true, false, @"C:\Program Files\Netbird\netbird.exe", string.Empty, string.Empty,
        false, false, string.Empty, string.Empty);

    internal static WindowsNetBirdProtectedFileSnapshot MissingFile() => new(
        NetBirdProtectedFileState.Missing);
}

internal sealed class FakeWindowsNetBirdStateEnvironment(
    WindowsNetBirdSnapshot initial) : IWindowsNetBirdStateEnvironment
{
    internal WindowsNetBirdSnapshot Current { get; set; } = initial;
    internal byte[]? WrittenMarker { get; private set; }
    internal int DeleteCalls { get; private set; }
    internal int RestoreCalls { get; private set; }
    public string ConfigurationPath => @"C:\ProgramData\Netbird\config.json";

    public WindowsNetBirdSnapshot Capture() => Current;

    public void WriteOwnerMarker(byte[] content)
    {
        WrittenMarker = content.ToArray();
        Current = Current with
        {
            OwnerMarker = new WindowsNetBirdProtectedFileSnapshot(
                NetBirdProtectedFileState.Present,
                content.ToArray()),
            EvidenceId = Current.EvidenceId + "|marker",
        };
    }

    public void DeleteOwnerMarker()
    {
        DeleteCalls++;
        Current = Current with
        {
            OwnerMarker = WindowsNetBirdStateTestSupport.MissingFile(),
            EvidenceId = Current.EvidenceId + "|deleted",
        };
    }

    public void RestoreConfiguration(string protectedSourcePath, string expectedSha256)
    {
        RestoreCalls++;
        Current = Current with
        {
            Configuration = new WindowsNetBirdProtectedFileSnapshot(
                NetBirdProtectedFileState.Present,
                null,
                expectedSha256),
            EvidenceId = Current.EvidenceId + "|config",
        };
    }
}

internal sealed class FakeVerifiedPackageLease : IVerifiedNetBirdPackageLease
{
    public string HandleId => "netbird-msi-v1:sha256:" + WindowsNetBirdStateTestSupport.InstallerHash.ToLowerInvariant();
    public string StagedPath => @"C:\ProgramData\StructuraConnectorInstaller\NetBirdPackages\netbird.msi";
    public RollbackPayloadProtection Protection => RollbackPayloadProtection.ProtectedMachineStaging;
    public NetBirdMsiPackageIdentity Package => WindowsNetBirdStateTestSupport.Package;
    public long SizeBytes => WindowsNetBirdStateTestSupport.Pin.InstallerBytes;
    public string Sha256 => WindowsNetBirdStateTestSupport.InstallerHash;
    public bool AuthenticodeTrusted => true;
    public string SignerSubject => "CN=NetBird GmbH, O=NetBird GmbH";
    public string SignerThumbprint => WindowsNetBirdStateTestSupport.Pin.SignerThumbprint;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeWindowsRestoreLease : IWindowsVerifiedNetBirdRestorePointLease
{
    internal bool Disposed { get; private set; }
    internal string OwnerOperationId { get; init; } = WindowsNetBirdStateTestSupport.PriorOperationId;
    internal NetBirdOwnedState OwnedState { get; init; } = WindowsNetBirdStateTestSupport.Marker().OwnedState;
    internal NetBirdMsiPackageIdentity LeasePackage { get; init; } = WindowsNetBirdStateTestSupport.Package;
    internal string InstallerSha { get; init; } = WindowsNetBirdStateTestSupport.InstallerHash;
    internal string Handle { get; init; } = "netbird-restore-v1:44444444444444444444444444444444";

    public NetBirdOwnedRestorePoint RestorePoint => new(
        Handle,
        OwnedState,
        RollbackPayloadProtection.ProtectedMachineStaging,
        LeasePackage,
        InstallerSha);
    public NetBirdMsiPackageIdentity Package => LeasePackage;
    public string StagedPath => @"C:\ProgramData\StructuraConnectorInstaller\NetBirdRestorePoints\prior.msi";
    public long SizeBytes => 123;
    public string Sha256 => InstallerSha;
    public bool AuthenticodeTrusted => true;
    public string SignerSubject => "CN=NetBird GmbH, O=NetBird GmbH";
    public string SignerThumbprint => WindowsNetBirdStateTestSupport.Pin.SignerThumbprint;
    public string ProtectedConfigurationPath =>
        @"C:\ProgramData\StructuraConnectorInstaller\NetBirdRestorePoints\config.json";
    public string PriorOwnerOperationId => OwnerOperationId;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeRestoreBackend(FakeWindowsRestoreLease lease) : IWindowsNetBirdRestoreBackend
{
    internal FakeWindowsRestoreLease Lease { get; set; } = lease;
    internal int CaptureCalls { get; private set; }
    internal int ReacquireCalls { get; private set; }

    public ValueTask<IWindowsVerifiedNetBirdRestorePointLease> CaptureAsync(
        NetBirdMachineInspection exactOwnedInstallation,
        CancellationToken cancellationToken)
    {
        CaptureCalls++;
        return ValueTask.FromResult<IWindowsVerifiedNetBirdRestorePointLease>(Lease);
    }

    public ValueTask<IWindowsVerifiedNetBirdRestorePointLease> ReacquireAsync(
        NetBirdOwnedRestorePoint restorePoint,
        CancellationToken cancellationToken)
    {
        ReacquireCalls++;
        return ValueTask.FromResult<IWindowsVerifiedNetBirdRestorePointLease>(Lease);
    }
}
