using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;
using Connector.Upgrade.WindowsMsi;

namespace Connector.Upgrade.NetBirdMachine.Tests;

internal sealed class NetBirdMachineFixture
{
    internal static readonly string TargetHash = new('5', 64);
    internal static readonly string PriorHash = new('4', 64);
    internal static readonly NetBirdMsiPackageIdentity TargetPackage = new(
        Guid.Parse("463D0C9D-ED41-451E-A44F-937932C8B267"),
        Guid.Parse("6456EC4E-3AD6-4B9B-A2BE-98E81CB21CCF"),
        "0.79.0",
        "NetBird GmbH",
        "NetBird");
    internal static readonly NetBirdMsiPackageIdentity PriorPackage = new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Guid.Parse("6456EC4E-3AD6-4B9B-A2BE-98E81CB21CCF"),
        "0.78.0",
        "NetBird GmbH",
        "NetBird");
    internal static readonly NetBirdOwnedState TargetState = new(
        "target-installation",
        "0.79.0",
        new string('B', 64),
        "NetBird|C:\\Program Files\\NetBird\\netbird.exe|2");
    internal static readonly NetBirdOwnedState PriorState = new(
        "prior-installation",
        "0.78.0",
        new string('A', 64),
        "NetBird|C:\\Program Files\\NetBird\\netbird.exe|2");

    internal OfficialNetBirdPackagePin Pin { get; } = new(
        "0.79.0",
        "0.76.0",
        new Uri("https://github.com/netbirdio/netbird/releases/tag/v0.79.0"),
        "netbird_installer_0.79.0_windows_amd64.msi",
        23543808,
        TargetHash,
        "CN=NetBird GmbH,",
        "7B41FCCAFCB794720FE07D381F9CBDF18AB5900F");

    internal FakeElevationGate Elevation { get; } = new();
    internal FakePackageStager Stager { get; }
    internal FakeMachineStatePort State { get; } = new();
    internal FakeRestorePointStore RestorePoints { get; }
    internal FakeNetBirdMsiRunner Msi { get; } = new();
    internal NetBirdMachineInstallService Service { get; }

    internal NetBirdMachineFixture()
    {
        Stager = new FakePackageStager(TargetLease());
        RestorePoints = new FakeRestorePointStore(PriorRestoreLease());
        Service = new NetBirdMachineInstallService(Elevation, Stager, State, RestorePoints, Msi, Pin);
        State.Verification = new NetBirdInstalledServiceVerification(
            true,
            TargetPackage,
            TargetState.ServiceIdentity,
            "service-readback");
    }

    internal static NetBirdAssessment AbsentAssessment() => new(NetBirdOwnership.Absent, null);

    internal static NetBirdAssessment OwnedAssessment(NetBirdOwnedState state) =>
        new(NetBirdOwnership.OwnedByConnector, state.InstallationId, state);

    internal static NetBirdMachineInspection AbsentInspection() =>
        new(true, AbsentAssessment(), null, null, false, "absent-evidence");

    internal static NetBirdMachineInspection OwnedInspection(
        NetBirdOwnedState state,
        NetBirdMsiPackageIdentity package,
        string hash,
        string? ownerOperationId = null) =>
        new(true, OwnedAssessment(state), package, hash, true, "owned-evidence", ownerOperationId);

    internal static NetBirdMachineInspection ForeignInspection() =>
        new(true, new NetBirdAssessment(NetBirdOwnership.Foreign, "foreign"), PriorPackage, PriorHash, true, "foreign-evidence");

    internal static NetBirdMachineInspection UnattributedTargetInspection() =>
        new(true, new NetBirdAssessment(NetBirdOwnership.Unattributed, null), TargetPackage, TargetHash, true, "unattributed-target");

    internal FakeVerifiedPackageLease TargetLease() => new()
    {
        HandleId = "netbird-target-v1:079",
        StagedPath = $"C:\\ProgramData\\StructuraConnectorInstaller\\NetBird\\{Pin.InstallerName}",
        Package = TargetPackage,
        SizeBytes = Pin.InstallerBytes,
        Sha256 = Pin.InstallerSha256,
        AuthenticodeTrusted = true,
        SignerSubject = "CN=NetBird GmbH, O=NetBird GmbH, C=DE",
        SignerThumbprint = Pin.SignerThumbprint,
    };

    internal static FakeRestorePointLease PriorRestoreLease() => new()
    {
        RestorePoint = new NetBirdOwnedRestorePoint(
            "netbird-restore-v1:prior",
            PriorState,
            RollbackPayloadProtection.ProtectedMachineStaging,
            PriorPackage,
            PriorHash),
        Package = PriorPackage,
        StagedPath = "C:\\ProgramData\\StructuraConnectorInstaller\\NetBirdRestore\\prior.msi",
        SizeBytes = 1024,
        Sha256 = PriorHash,
        AuthenticodeTrusted = true,
        SignerSubject = "CN=NetBird GmbH, O=NetBird GmbH, C=DE",
        SignerThumbprint = "7B41FCCAFCB794720FE07D381F9CBDF18AB5900F",
    };
}

internal sealed class FakeElevationGate : ITrustedElevatedCallerGate
{
    internal int Calls { get; private set; }
    internal Exception? Failure { get; set; }
    public void AssertTrustedElevatedCaller()
    {
        Calls++;
        if (Failure is not null)
            throw Failure;
    }
}

internal sealed class FakePackageStager(FakeVerifiedPackageLease lease) : IVerifiedNetBirdPackageStager
{
    internal int Calls { get; private set; }
    internal int ReacquireCalls { get; private set; }
    internal FakeVerifiedPackageLease Lease { get; set; } = lease;

    public ValueTask<IVerifiedNetBirdPackageLease> StageAndVerifyAsync(
        string sourceMsiPath,
        OfficialNetBirdPackagePin pin,
        CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromResult<IVerifiedNetBirdPackageLease>(Lease);
    }

    public ValueTask<IVerifiedNetBirdPackageLease> ReacquireAsync(
        ProtectedNetBirdPackageReceipt receipt,
        OfficialNetBirdPackagePin pin,
        CancellationToken cancellationToken)
    {
        ReacquireCalls++;
        return ValueTask.FromResult<IVerifiedNetBirdPackageLease>(Lease);
    }
}

internal sealed class FakeVerifiedPackageLease : IVerifiedNetBirdPackageLease
{
    public required string HandleId { get; init; }
    public required string StagedPath { get; init; }
    public RollbackPayloadProtection Protection { get; init; } = RollbackPayloadProtection.ProtectedMachineStaging;
    public required NetBirdMsiPackageIdentity Package { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required bool AuthenticodeTrusted { get; init; }
    public required string SignerSubject { get; init; }
    public required string SignerThumbprint { get; init; }
    internal bool Disposed { get; private set; }
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeMachineStatePort : INetBirdMachineStatePort
{
    internal Queue<NetBirdMachineInspection> Inspections { get; } = new();
    internal Queue<NetBirdInstalledServiceVerification> Verifications { get; } = new();
    internal NetBirdInstalledServiceVerification? Verification { get; set; }
    internal bool PackageAbsent { get; set; } = true;
    internal int InspectCalls { get; private set; }
    internal int VerifyServiceCalls { get; private set; }
    internal int PublishCalls { get; private set; }
    internal int RemoveOwnershipCalls { get; private set; }
    internal int RestoreOwnershipCalls { get; private set; }

    public ValueTask<NetBirdMachineInspection> InspectAsync(CancellationToken cancellationToken)
    {
        InspectCalls++;
        return ValueTask.FromResult(Inspections.Dequeue());
    }

    public ValueTask<NetBirdInstalledServiceVerification> VerifyInstalledPackageAndServiceAsync(
        NetBirdMsiPackageIdentity expectedPackage,
        CancellationToken cancellationToken)
    {
        VerifyServiceCalls++;
        return ValueTask.FromResult(
            Verifications.Count > 0
                ? Verifications.Dequeue()
                : Verification ?? new(false, expectedPackage, string.Empty, "failed"));
    }

    public ValueTask<bool> VerifyPackageAbsentAsync(
        NetBirdMsiPackageIdentity expectedPackage,
        CancellationToken cancellationToken) => ValueTask.FromResult(PackageAbsent);

    public ValueTask PublishOwnershipAsync(
        string operationId,
        IVerifiedNetBirdPackageLease package,
        NetBirdInstalledServiceVerification service,
        CancellationToken cancellationToken)
    {
        PublishCalls++;
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveOwnershipAsync(NetBirdOwnedState exactState, CancellationToken cancellationToken)
    {
        RemoveOwnershipCalls++;
        return ValueTask.CompletedTask;
    }

    public ValueTask RestoreOwnershipAsync(
        IVerifiedNetBirdRestorePointLease restorePoint,
        NetBirdInstalledServiceVerification service,
        CancellationToken cancellationToken)
    {
        RestoreOwnershipCalls++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeRestorePointStore(FakeRestorePointLease restoreLease) : INetBirdOwnedRestorePointStore
{
    internal FakeRestorePointLease RestoreLease { get; set; } = restoreLease;
    internal int CaptureCalls { get; private set; }
    internal int ReacquireCalls { get; private set; }

    public ValueTask<IVerifiedNetBirdRestorePointLease> CaptureAsync(
        NetBirdMachineInspection exactOwnedInstallation,
        CancellationToken cancellationToken)
    {
        CaptureCalls++;
        return ValueTask.FromResult<IVerifiedNetBirdRestorePointLease>(RestoreLease);
    }

    public ValueTask<IVerifiedNetBirdRestorePointLease> ReacquireAsync(
        NetBirdOwnedRestorePoint restorePoint,
        CancellationToken cancellationToken)
    {
        ReacquireCalls++;
        return ValueTask.FromResult<IVerifiedNetBirdRestorePointLease>(RestoreLease);
    }
}

internal sealed class FakeRestorePointLease : IVerifiedNetBirdRestorePointLease
{
    public required NetBirdOwnedRestorePoint RestorePoint { get; init; }
    public required NetBirdMsiPackageIdentity Package { get; init; }
    public required string StagedPath { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required bool AuthenticodeTrusted { get; init; }
    public required string SignerSubject { get; init; }
    public required string SignerThumbprint { get; init; }
    internal bool Disposed { get; private set; }
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeNetBirdMsiRunner : INetBirdMsiMutationRunner
{
    internal WindowsMsiProcessResult Result { get; set; } =
        new(WindowsMsiProcessCompletion.Exited, 0);
    internal int InstallCalls { get; private set; }
    internal int UninstallCalls { get; private set; }
    internal string? InstalledPath { get; private set; }
    internal Guid? UninstalledProduct { get; private set; }

    public ValueTask<WindowsMsiProcessResult> InstallAsync(
        string protectedMsiPath,
        CancellationToken cancellationToken)
    {
        InstallCalls++;
        InstalledPath = protectedMsiPath;
        return ValueTask.FromResult(Result);
    }

    public ValueTask<WindowsMsiProcessResult> UninstallExactAsync(
        Guid productCode,
        CancellationToken cancellationToken)
    {
        UninstallCalls++;
        UninstalledProduct = productCode;
        return ValueTask.FromResult(Result);
    }
}
