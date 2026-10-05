using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;
using Connector.Upgrade.WindowsMsi;

namespace Connector.Upgrade.NetBirdMachine.Tests;

public sealed class NetBirdMachineInstallServiceTests
{
    private const string SourceMsi = "C:\\PackageSource\\netbird_installer_0.79.0_windows_amd64.msi";
    private static readonly Guid OperationId = Guid.Parse("7c995af0-b056-4d50-a258-af805fa0b991");

    [Fact]
    public void ConstructionHasNoMachineSideEffects()
    {
        var fixture = new NetBirdMachineFixture();

        Assert.Equal(0, fixture.Elevation.Calls);
        Assert.Equal(0, fixture.State.InspectCalls);
        Assert.Equal(0, fixture.Stager.Calls);
        Assert.Equal(0, fixture.Msi.InstallCalls);
        Assert.Equal(0, fixture.Msi.UninstallCalls);
    }

    [Fact]
    public async Task RequiresAlreadyElevatedTrustedCallerBeforeInspectionOrStaging()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.Elevation.Failure = new UnauthorizedAccessException("not elevated");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.Service.EnsureOwnedAsync(NetBirdMachineFixture.AbsentAssessment(), SourceMsi));

        Assert.Equal(0, fixture.State.InspectCalls);
        Assert.Equal(0, fixture.Stager.Calls);
    }

    [Fact]
    public async Task PreservesForeignInstallationWithoutStagingOrMutation()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.ForeignInspection());

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await fixture.Service.EnsureOwnedAsync(NetBirdMachineFixture.AbsentAssessment(), SourceMsi));

        Assert.Equal(0, fixture.Stager.Calls);
        Assert.Equal(0, fixture.Msi.InstallCalls);
        Assert.Equal(0, fixture.RestorePoints.CaptureCalls);
    }

    [Fact]
    public async Task RejectsPreflightDriftBeforeStagingOrMutation()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorHash));

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await fixture.Service.EnsureOwnedAsync(NetBirdMachineFixture.AbsentAssessment(), SourceMsi));

        Assert.Equal(0, fixture.Stager.Calls);
        Assert.Equal(0, fixture.Msi.InstallCalls);
    }

    [Fact]
    public async Task RejectsStateChangeDuringProtectedStagingBeforeMutation()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.ForeignInspection());

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await fixture.Service.EnsureOwnedAsync(NetBirdMachineFixture.AbsentAssessment(), SourceMsi));

        Assert.Equal(1, fixture.Stager.Calls);
        Assert.Equal(0, fixture.Msi.InstallCalls);
    }

    [Fact]
    public async Task MapsExactPreexistingOwnedTargetToNoChange()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256));
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256));

        var receipt = await fixture.Service.EnsureOwnedAsync(
            NetBirdMachineFixture.OwnedAssessment(NetBirdMachineFixture.TargetState), SourceMsi);

        var noChange = Assert.IsType<NetBirdNoChangeReceipt>(receipt);
        Assert.Equal(NetBirdMachineFixture.TargetState, noChange.ExistingState);
        Assert.Equal(0, fixture.Msi.InstallCalls);
        Assert.Equal(0, fixture.RestorePoints.CaptureCalls);
    }

    [Fact]
    public async Task InstallsAbsentAndReturnsTypedInstalledThisRunReceiptAfterExactReadback()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256));

        var receipt = await fixture.Service.EnsureOwnedAsync(NetBirdMachineFixture.AbsentAssessment(), SourceMsi);

        var installed = Assert.IsType<NetBirdInstalledThisRunReceipt>(receipt);
        Assert.False(string.IsNullOrWhiteSpace(installed.OperationId));
        Assert.Equal(NetBirdMachineFixture.TargetState, installed.InstalledState);
        Assert.Equal(1, fixture.Msi.InstallCalls);
        Assert.Equal(1, fixture.State.VerifyServiceCalls);
        Assert.Equal(1, fixture.State.PublishCalls);
    }

    [Fact]
    public async Task PreservesMsi3010InTheOwnedMutationReceipt()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.Msi.Result = new WindowsMsiProcessResult(WindowsMsiProcessCompletion.Exited, 3010);
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState, NetBirdMachineFixture.TargetPackage, fixture.Pin.InstallerSha256));

        var receipt = await fixture.Service.EnsureOwnedAsync(NetBirdMachineFixture.AbsentAssessment(), SourceMsi);

        Assert.True(Assert.IsType<NetBirdInstalledThisRunReceipt>(receipt).RebootRequired);
    }

    [Theory]
    [InlineData("protection")]
    [InlineData("hash")]
    [InlineData("signature")]
    [InlineData("version")]
    public async Task RejectsUnverifiedTargetLeaseBeforeMsi(string mutation)
    {
        var fixture = new NetBirdMachineFixture();
        var valid = fixture.TargetLease();
        fixture.Stager.Lease = mutation switch
        {
            "protection" => valid.withProtection(RollbackPayloadProtection.UnprotectedOrUserWritable),
            "hash" => valid.withHash(new string('0', 64)),
            "signature" => valid.withSignature(false),
            "version" => valid.withPackage(valid.Package with { ProductVersion = "0.80.0" }),
            _ => valid,
        };
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await fixture.Service.EnsureOwnedAsync(NetBirdMachineFixture.AbsentAssessment(), SourceMsi));

        Assert.Equal(0, fixture.Msi.InstallCalls);
    }

    [Fact]
    public async Task CapturesExactProtectedPriorStateBeforeUpdating()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorHash));
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorHash));
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorHash));
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256));

        var receipt = await fixture.Service.EnsureOwnedAsync(
            NetBirdMachineFixture.OwnedAssessment(NetBirdMachineFixture.PriorState), SourceMsi);

        var updated = Assert.IsType<NetBirdUpdatedThisRunReceipt>(receipt);
        Assert.Equal(NetBirdMachineFixture.PriorState, updated.PriorOwnedState);
        Assert.Equal(NetBirdMachineFixture.TargetState, updated.UpdatedOwnedState);
        Assert.Equal(RollbackPayloadProtection.ProtectedMachineStaging, updated.PriorRestorePoint.Protection);
        Assert.Equal(1, fixture.RestorePoints.CaptureCalls);
        Assert.Equal(1, fixture.Msi.InstallCalls);
    }

    [Fact]
    public async Task InterruptedAbsentInstallPreservesExactButUnattributedPackage()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        var plan = await fixture.Service.PrepareOwnedMutationAsync(
            NetBirdMachineFixture.AbsentAssessment(), OperationId, SourceMsi);

        Assert.Equal(NetBirdChangeKind.InstalledThisRun, plan.Change);
        Assert.Equal(OperationId.ToString("D"), plan.OperationId);
        Assert.Equal(NetBirdMachineFixture.TargetPackage, plan.TargetPackage.Package);
        Assert.Equal(0, fixture.Msi.InstallCalls);

        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.UnattributedTargetInspection());

        await Assert.ThrowsAsync<NetBirdManualRecoveryRequiredException>(async () =>
            await fixture.Service.ReconcileInterruptedMutationAsync(plan));

        Assert.Equal(0, fixture.Msi.UninstallCalls);
        Assert.Equal(0, fixture.State.RemoveOwnershipCalls);
    }

    [Fact]
    public async Task Prepared_no_change_preserves_null_mutation_operation_id_and_exact_owned_assessment()
    {
        var fixture = new NetBirdMachineFixture();
        var assessment = NetBirdMachineFixture.OwnedAssessment(NetBirdMachineFixture.TargetState);
        var exact = NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256);
        fixture.State.Inspections.Enqueue(exact);
        fixture.State.Inspections.Enqueue(exact);

        var plan = await fixture.Service.PrepareOwnedMutationAsync(assessment, OperationId, SourceMsi);

        Assert.Equal(NetBirdChangeKind.NoChange, plan.Change);
        Assert.Null(plan.OperationId);
        Assert.Equal(assessment, plan.PriorAssessment);
        Assert.Equal(0, fixture.Msi.InstallCalls);
        Assert.Equal(0, fixture.Msi.UninstallCalls);
    }

    [Fact]
    public async Task RejectsEmptyTrustedOperationIdBeforeInspectionOrStaging()
    {
        var fixture = new NetBirdMachineFixture();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await fixture.Service.PrepareOwnedMutationAsync(NetBirdMachineFixture.AbsentAssessment(), Guid.Empty, SourceMsi));

        Assert.Equal(0, fixture.State.InspectCalls);
        Assert.Equal(0, fixture.Stager.Calls);
    }

    [Fact]
    public async Task RecoversAbsentInstallOnlyWhenOwnerMarkerBindsDurableOperationId()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        var plan = await fixture.Service.PrepareOwnedMutationAsync(
            NetBirdMachineFixture.AbsentAssessment(), OperationId, SourceMsi);
        Assert.Equal(OperationId.ToString("D"), plan.OperationId);
        var attributed = NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256,
            plan.OperationId);
        fixture.State.Inspections.Enqueue(attributed);
        fixture.State.Inspections.Enqueue(attributed);
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());

        var recovered = await fixture.Service.ReconcileInterruptedMutationAsync(plan);

        Assert.Equal(NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun, recovered.Action);
        Assert.Equal(NetBirdMachineFixture.TargetPackage.ProductCode, fixture.Msi.UninstalledProduct);
        Assert.Equal(1, fixture.State.RemoveOwnershipCalls);
    }

    [Fact]
    public async Task RecoversOwnedUpdateAfterMsiByRestoringExactPriorProtectedState()
    {
        var fixture = new NetBirdMachineFixture();
        var prior = NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorHash);
        fixture.State.Inspections.Enqueue(prior);
        fixture.State.Inspections.Enqueue(prior);
        fixture.State.Inspections.Enqueue(prior);
        var plan = await fixture.Service.PrepareOwnedMutationAsync(
            NetBirdMachineFixture.OwnedAssessment(NetBirdMachineFixture.PriorState), OperationId, SourceMsi);

        Assert.Equal(NetBirdChangeKind.UpdatedThisRun, plan.Change);
        Assert.Equal(OperationId.ToString("D"), plan.OperationId);
        Assert.Equal(NetBirdMachineFixture.PriorPackage, plan.PriorRestorePoint!.Package);
        Assert.Equal(0, fixture.Msi.InstallCalls);

        var interrupted = NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256,
            plan.OperationId);
        fixture.State.Inspections.Enqueue(interrupted);
        fixture.State.Inspections.Enqueue(interrupted);
        fixture.State.Inspections.Enqueue(prior);
        fixture.State.Verifications.Enqueue(new(
            true,
            NetBirdMachineFixture.TargetPackage,
            NetBirdMachineFixture.TargetState.ServiceIdentity,
            "target-after-crash"));
        fixture.State.Verifications.Enqueue(new(
            true,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorState.ServiceIdentity,
            "prior-after-restore"));
        var recovered = await fixture.Service.ReconcileInterruptedMutationAsync(plan);

        Assert.Equal(NetBirdInterruptedRecoveryAction.RestoredPriorOwnedState, recovered.Action);
        Assert.Equal(fixture.RestorePoints.RestoreLease.StagedPath, fixture.Msi.InstalledPath);
        Assert.Equal(1, fixture.State.RestoreOwnershipCalls);
    }

    [Fact]
    public async Task InterruptedUpdatePreservesMatchingMsiWithoutThisOperationMarker()
    {
        var fixture = new NetBirdMachineFixture();
        var prior = NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorHash);
        fixture.State.Inspections.Enqueue(prior);
        fixture.State.Inspections.Enqueue(prior);
        fixture.State.Inspections.Enqueue(prior);
        var plan = await fixture.Service.PrepareOwnedMutationAsync(
            NetBirdMachineFixture.OwnedAssessment(NetBirdMachineFixture.PriorState), OperationId, SourceMsi);

        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256));

        await Assert.ThrowsAsync<NetBirdManualRecoveryRequiredException>(async () =>
            await fixture.Service.ReconcileInterruptedMutationAsync(plan));

        Assert.Equal(0, fixture.Msi.InstallCalls);
        Assert.Equal(0, fixture.Msi.UninstallCalls);
        Assert.Equal(0, fixture.State.RestoreOwnershipCalls);
    }

    [Fact]
    public async Task InterruptedInstallPreservesUncertainForeignStateAndFailsClosed()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        var plan = await fixture.Service.PrepareOwnedMutationAsync(
            NetBirdMachineFixture.AbsentAssessment(), OperationId, SourceMsi);
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.ForeignInspection());

        await Assert.ThrowsAsync<NetBirdManualRecoveryRequiredException>(async () =>
            await fixture.Service.ReconcileInterruptedMutationAsync(plan));

        Assert.Equal(0, fixture.Msi.InstallCalls);
        Assert.Equal(0, fixture.Msi.UninstallCalls);
        Assert.Equal(0, fixture.State.RemoveOwnershipCalls);
    }

    [Fact]
    public async Task RejectsMismatchedPriorRestorePointBeforeUpdate()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.RestorePoints.RestoreLease = NetBirdMachineFixture.PriorRestoreLease().withHash(new string('9', 64));
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorHash));
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorHash));

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await fixture.Service.EnsureOwnedAsync(
                NetBirdMachineFixture.OwnedAssessment(NetBirdMachineFixture.PriorState), SourceMsi));

        Assert.Equal(0, fixture.Msi.InstallCalls);
    }

    [Fact]
    public async Task RejectsOwnedStateChangeAfterRestoreCaptureBeforeUpdate()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorHash));
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorHash));
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.ForeignInspection());

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await fixture.Service.EnsureOwnedAsync(
                NetBirdMachineFixture.OwnedAssessment(NetBirdMachineFixture.PriorState), SourceMsi));

        Assert.Equal(1, fixture.RestorePoints.CaptureCalls);
        Assert.Equal(0, fixture.Msi.InstallCalls);
    }

    [Theory]
    [InlineData(WindowsMsiProcessCompletion.TimedOut, null)]
    [InlineData(WindowsMsiProcessCompletion.Cancelled, null)]
    [InlineData(WindowsMsiProcessCompletion.StartFailed, null)]
    [InlineData(WindowsMsiProcessCompletion.Exited, 1603)]
    public async Task TreatsUncertainOrFailedInstallAsManualRecovery(
        WindowsMsiProcessCompletion completion,
        int? exitCode)
    {
        var fixture = new NetBirdMachineFixture();
        fixture.Msi.Result = new WindowsMsiProcessResult(completion, exitCode);
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());

        await Assert.ThrowsAsync<NetBirdManualRecoveryRequiredException>(async () =>
            await fixture.Service.EnsureOwnedAsync(NetBirdMachineFixture.AbsentAssessment(), SourceMsi));

        Assert.Equal(0, fixture.State.PublishCalls);
    }

    [Fact]
    public async Task RequiresExactServiceIdentityAfterInstall()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Verification = new(false, NetBirdMachineFixture.TargetPackage, "wrong", "probe");
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());

        await Assert.ThrowsAsync<NetBirdManualRecoveryRequiredException>(async () =>
            await fixture.Service.EnsureOwnedAsync(NetBirdMachineFixture.AbsentAssessment(), SourceMsi));

        Assert.Equal(1, fixture.Msi.InstallCalls);
        Assert.Equal(0, fixture.State.PublishCalls);
    }

    [Fact]
    public async Task RollbackInstalledThisRunUninstallsOnlyExactCurrentOwnedPackage()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256,
            "op"));
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256,
            "op"));
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.AbsentInspection());
        var receipt = new NetBirdInstalledThisRunReceipt("op", NetBirdMachineFixture.TargetState);

        await fixture.Service.RemoveInstalledThisRunAsync(receipt);

        Assert.Equal(NetBirdMachineFixture.TargetPackage.ProductCode, fixture.Msi.UninstalledProduct);
        Assert.Equal(1, fixture.State.RemoveOwnershipCalls);
    }

    [Fact]
    public async Task RollbackInstalledThisRunPreservesForeignOrChangedInstall()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.ForeignInspection());
        var receipt = new NetBirdInstalledThisRunReceipt("op", NetBirdMachineFixture.TargetState);

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await fixture.Service.RemoveInstalledThisRunAsync(receipt));

        Assert.Equal(0, fixture.Msi.UninstallCalls);
        Assert.Equal(0, fixture.State.RemoveOwnershipCalls);
    }

    [Fact]
    public async Task RollbackInstalledThisRunRequiresMatchingLiveOwnerOperationId()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256,
            "other-op"));
        var receipt = new NetBirdInstalledThisRunReceipt("op", NetBirdMachineFixture.TargetState);

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await fixture.Service.RemoveInstalledThisRunAsync(receipt));

        Assert.Equal(0, fixture.Msi.UninstallCalls);
        Assert.Equal(0, fixture.State.RemoveOwnershipCalls);
    }

    [Fact]
    public async Task UncertainUninstallKeepsOwnerMarkerAndRequiresManualRecovery()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.Msi.Result = new(WindowsMsiProcessCompletion.TimedOut);
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256,
            "op"));
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256,
            "op"));
        var receipt = new NetBirdInstalledThisRunReceipt("op", NetBirdMachineFixture.TargetState);

        await Assert.ThrowsAsync<NetBirdManualRecoveryRequiredException>(async () =>
            await fixture.Service.RemoveInstalledThisRunAsync(receipt));

        Assert.Equal(0, fixture.State.RemoveOwnershipCalls);
    }

    [Fact]
    public async Task RollbackUpdatedThisRunRestoresExactProtectedPriorOwnedState()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256,
            "update-op"));
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256,
            "update-op"));
        fixture.State.Verification = new(
            true,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorState.ServiceIdentity,
            "prior-service-readback");
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorHash));
        var restore = fixture.RestorePoints.RestoreLease.RestorePoint;
        var receipt = new NetBirdUpdatedThisRunReceipt(
            "update-op",
            restore,
            NetBirdMachineFixture.TargetState);

        await fixture.Service.RestoreUpdatedThisRunAsync(receipt);

        Assert.Equal(1, fixture.RestorePoints.ReacquireCalls);
        Assert.Equal(fixture.RestorePoints.RestoreLease.StagedPath, fixture.Msi.InstalledPath);
        Assert.Equal(1, fixture.State.RestoreOwnershipCalls);
        Assert.Equal(0, fixture.Msi.UninstallCalls);
    }

    [Fact]
    public async Task RollbackUpdatedThisRunRequiresMatchingLiveOwnerOperationId()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState,
            NetBirdMachineFixture.TargetPackage,
            fixture.Pin.InstallerSha256,
            "other-op"));
        var receipt = new NetBirdUpdatedThisRunReceipt(
            "update-op",
            fixture.RestorePoints.RestoreLease.RestorePoint,
            NetBirdMachineFixture.TargetState);

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await fixture.Service.RestoreUpdatedThisRunAsync(receipt));

        Assert.Equal(0, fixture.RestorePoints.ReacquireCalls);
        Assert.Equal(0, fixture.Msi.InstallCalls);
    }

    [Fact]
    public async Task RollbackUpdatedThisRunPreservesPreexistingChangedState()
    {
        var fixture = new NetBirdMachineFixture();
        fixture.State.Inspections.Enqueue(NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.PriorState,
            NetBirdMachineFixture.PriorPackage,
            NetBirdMachineFixture.PriorHash));
        var receipt = new NetBirdUpdatedThisRunReceipt(
            "update-op",
            fixture.RestorePoints.RestoreLease.RestorePoint,
            NetBirdMachineFixture.TargetState);

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await fixture.Service.RestoreUpdatedThisRunAsync(receipt));

        Assert.Equal(0, fixture.RestorePoints.ReacquireCalls);
        Assert.Equal(0, fixture.Msi.InstallCalls);
    }
}

internal static class TestLeaseMutations
{
    internal static FakeVerifiedPackageLease withProtection(
        this FakeVerifiedPackageLease source,
        RollbackPayloadProtection protection) => new()
    {
        HandleId = source.HandleId,
        StagedPath = source.StagedPath,
        Protection = protection,
        Package = source.Package,
        SizeBytes = source.SizeBytes,
        Sha256 = source.Sha256,
        AuthenticodeTrusted = source.AuthenticodeTrusted,
        SignerSubject = source.SignerSubject,
        SignerThumbprint = source.SignerThumbprint,
    };

    internal static FakeVerifiedPackageLease withHash(this FakeVerifiedPackageLease source, string hash) => new()
    {
        HandleId = source.HandleId,
        StagedPath = source.StagedPath,
        Protection = source.Protection,
        Package = source.Package,
        SizeBytes = source.SizeBytes,
        Sha256 = hash,
        AuthenticodeTrusted = source.AuthenticodeTrusted,
        SignerSubject = source.SignerSubject,
        SignerThumbprint = source.SignerThumbprint,
    };

    internal static FakeVerifiedPackageLease withSignature(this FakeVerifiedPackageLease source, bool trusted) => new()
    {
        HandleId = source.HandleId,
        StagedPath = source.StagedPath,
        Protection = source.Protection,
        Package = source.Package,
        SizeBytes = source.SizeBytes,
        Sha256 = source.Sha256,
        AuthenticodeTrusted = trusted,
        SignerSubject = source.SignerSubject,
        SignerThumbprint = source.SignerThumbprint,
    };

    internal static FakeVerifiedPackageLease withPackage(
        this FakeVerifiedPackageLease source,
        NetBirdMsiPackageIdentity package) => new()
    {
        HandleId = source.HandleId,
        StagedPath = source.StagedPath,
        Protection = source.Protection,
        Package = package,
        SizeBytes = source.SizeBytes,
        Sha256 = source.Sha256,
        AuthenticodeTrusted = source.AuthenticodeTrusted,
        SignerSubject = source.SignerSubject,
        SignerThumbprint = source.SignerThumbprint,
    };

    internal static FakeRestorePointLease withHash(this FakeRestorePointLease source, string hash) => new()
    {
        RestorePoint = source.RestorePoint,
        Package = source.Package,
        StagedPath = source.StagedPath,
        SizeBytes = source.SizeBytes,
        Sha256 = hash,
        AuthenticodeTrusted = source.AuthenticodeTrusted,
        SignerSubject = source.SignerSubject,
        SignerThumbprint = source.SignerThumbprint,
    };
}
