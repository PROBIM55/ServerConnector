using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsMsi;

namespace Connector.Upgrade.NetBirdMachine;

public sealed class NetBirdMachineInstallService
{
    private readonly OfficialNetBirdPackagePin _pin;
    private readonly ITrustedElevatedCallerGate _elevation;
    private readonly IVerifiedNetBirdPackageStager _stager;
    private readonly INetBirdMachineStatePort _state;
    private readonly INetBirdOwnedRestorePointStore _restorePoints;
    private readonly INetBirdMsiMutationRunner _msi;

    public NetBirdMachineInstallService(
        ITrustedElevatedCallerGate elevation,
        IVerifiedNetBirdPackageStager stager,
        INetBirdMachineStatePort state,
        INetBirdOwnedRestorePointStore restorePoints,
        INetBirdMsiMutationRunner msi,
        OfficialNetBirdPackagePin? pin = null)
    {
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        _stager = stager ?? throw new ArgumentNullException(nameof(stager));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _restorePoints = restorePoints ?? throw new ArgumentNullException(nameof(restorePoints));
        _msi = msi ?? throw new ArgumentNullException(nameof(msi));
        _pin = pin ?? OfficialNetBirdPackagePin.LoadEmbedded();
    }

    public async ValueTask<NetBirdMutationReceipt> EnsureOwnedAsync(
        NetBirdAssessment expectedAssessment,
        string sourceMsiPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedAssessment);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMsiPath);
        _elevation.AssertTrustedElevatedCaller();
        ValidateRequestedAssessment(expectedAssessment);

        var before = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
        RejectForeignOrUnattributed(before);
        RequireAssessmentMatch(expectedAssessment, before.Assessment);

        await using var target = await _stager
            .StageAndVerifyAsync(sourceMsiPath, _pin, cancellationToken)
            .ConfigureAwait(false);
        ValidateTargetLease(target);
        var ready = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
        RejectForeignOrUnattributed(ready);
        RequireSameObservedInstallation(before, ready);

        if (ready.Assessment.Ownership == NetBirdOwnership.OwnedByConnector &&
            IsExactPackage(ready, target.Package, target.Sha256))
            return new NetBirdNoChangeReceipt(ready.Assessment.OwnedState!);

        return ready.Assessment.Ownership switch
        {
            NetBirdOwnership.Absent => await InstallAbsentAsync(target, cancellationToken).ConfigureAwait(false),
            NetBirdOwnership.OwnedByConnector => await UpdateOwnedAsync(ready, target, cancellationToken).ConfigureAwait(false),
            _ => throw new NetBirdMachineInvariantException("Only an absent or exact Connector-owned NetBird can be changed."),
        };
    }

    public async ValueTask<NetBirdMutationPlan> PrepareOwnedMutationAsync(
        NetBirdAssessment expectedAssessment,
        Guid operationId,
        string sourceMsiPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedAssessment);
        if (operationId == Guid.Empty) throw new ArgumentException("A trusted operation id is required.", nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMsiPath);
        _elevation.AssertTrustedElevatedCaller();
        ValidateRequestedAssessment(expectedAssessment);

        var before = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
        RejectForeignOrUnattributed(before);
        RequireAssessmentMatch(expectedAssessment, before.Assessment);
        await using var target = await _stager
            .StageAndVerifyAsync(sourceMsiPath, _pin, cancellationToken)
            .ConfigureAwait(false);
        ValidateTargetLease(target);
        var ready = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
        RejectForeignOrUnattributed(ready);
        RequireSameObservedInstallation(before, ready);
        var targetReceipt = ToProtectedReceipt(target);

        if (ready.Assessment.Ownership == NetBirdOwnership.OwnedByConnector &&
            IsExactPackage(ready, target.Package, target.Sha256))
            return new NetBirdMutationPlan(
                NetBirdChangeKind.NoChange,
                null,
                ready.Assessment,
                targetReceipt,
                null);

        var operationIdText = operationId.ToString("D");
        if (ready.Assessment.Ownership == NetBirdOwnership.Absent)
            return new NetBirdMutationPlan(
                NetBirdChangeKind.InstalledThisRun,
                operationIdText,
                ready.Assessment,
                targetReceipt,
                null);

        await using var restore = await _restorePoints.CaptureAsync(ready, cancellationToken).ConfigureAwait(false);
        ValidateRestoreLease(restore, restore.RestorePoint, ready);
        var stable = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
        RequireSameObservedInstallation(ready, stable);
        return new NetBirdMutationPlan(
            NetBirdChangeKind.UpdatedThisRun,
            operationIdText,
            ready.Assessment,
            targetReceipt,
            restore.RestorePoint);
    }

    public async ValueTask<NetBirdMutationReceipt> ApplyPreparedMutationAsync(
        NetBirdMutationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _elevation.AssertTrustedElevatedCaller();
        ValidatePlan(plan);

        if (plan.Change == NetBirdChangeKind.NoChange)
        {
            var noChangeCurrent = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
            RequireAssessmentMatch(plan.PriorAssessment, noChangeCurrent.Assessment);
            if (!IsExactPackage(noChangeCurrent, plan.TargetPackage.Package, plan.TargetPackage.Sha256))
                throw new NetBirdMachineInvariantException("The prepared NetBird no-change state is no longer exact.");
            return new NetBirdNoChangeReceipt(noChangeCurrent.Assessment.OwnedState!);
        }

        await using var target = await _stager
            .ReacquireAsync(plan.TargetPackage, _pin, cancellationToken)
            .ConfigureAwait(false);
        ValidateTargetLease(target);
        RequireTargetReceiptMatch(plan.TargetPackage, target);
        var current = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
        RejectForeignOrUnattributed(current);
        RequireAssessmentMatch(plan.PriorAssessment, current.Assessment);

        if (plan.Change == NetBirdChangeKind.UpdatedThisRun)
            RequireInspectionMatchesRestorePoint(current, plan.PriorRestorePoint!);

        var (service, rebootRequired) = await InstallAndPublishAsync(plan.OperationId!, target, cancellationToken).ConfigureAwait(false);
        var after = await InspectCompleteAfterMutationAsync().ConfigureAwait(false);
        var resulting = RequireExactTargetOwned(after, target, service);
        return plan.Change switch
        {
            NetBirdChangeKind.InstalledThisRun => new NetBirdInstalledThisRunReceipt(plan.OperationId!, resulting)
                { RebootRequired = rebootRequired },
            NetBirdChangeKind.UpdatedThisRun => new NetBirdUpdatedThisRunReceipt(
                plan.OperationId!, plan.PriorRestorePoint!, resulting) { RebootRequired = rebootRequired },
            _ => throw new NetBirdMachineInvariantException("Unsupported prepared NetBird mutation."),
        };
    }

    public async ValueTask<NetBirdInterruptedRecoveryResult> ReconcileInterruptedMutationAsync(
        NetBirdMutationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _elevation.AssertTrustedElevatedCaller();
        ValidatePlan(plan);
        var current = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);

        if (plan.Change == NetBirdChangeKind.NoChange)
        {
            RequireAssessmentMatch(plan.PriorAssessment, current.Assessment);
            if (!IsExactPackage(current, plan.TargetPackage.Package, plan.TargetPackage.Sha256))
                throw new NetBirdManualRecoveryRequiredException("Prepared NetBird no-change state is no longer exact.");
            return new(NetBirdInterruptedRecoveryAction.NoMutationObserved, current.EvidenceId);
        }

        if (plan.Change == NetBirdChangeKind.InstalledThisRun)
            return await ReconcileInterruptedInstallAsync(plan, current, cancellationToken).ConfigureAwait(false);

        return await ReconcileInterruptedUpdateAsync(plan, current, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NetBirdInterruptedRecoveryResult> ReconcileInterruptedInstallAsync(
        NetBirdMutationPlan plan,
        NetBirdMachineInspection current,
        CancellationToken cancellationToken)
    {
        if (current.Assessment.Ownership == NetBirdOwnership.Absent)
            return new(NetBirdInterruptedRecoveryAction.NoMutationObserved, current.EvidenceId);
        if (current.Assessment.Ownership is NetBirdOwnership.Foreign or NetBirdOwnership.Unattributed)
            throw new NetBirdManualRecoveryRequiredException(
                "An installation without this operation's protected owner evidence is present; recovery preserved it.");
        if (current.Assessment.Ownership != NetBirdOwnership.OwnedByConnector ||
            !string.Equals(current.OwnerOperationId, plan.OperationId, StringComparison.Ordinal))
            throw new NetBirdManualRecoveryRequiredException(
                "The interrupted NetBird install is not causally bound to the durable operation id.");

        var service = await VerifyExactServiceAsync(plan.TargetPackage.Package, cancellationToken).ConfigureAwait(false);
        if (!IsExactPackage(current, plan.TargetPackage.Package, plan.TargetPackage.Sha256))
            throw new NetBirdManualRecoveryRequiredException("The installed NetBird package is not the exact planned payload.");
        if (current.Assessment.OwnedState is { } owned &&
            (!string.Equals(owned.Version, plan.TargetPackage.Package.ProductVersion, StringComparison.Ordinal) ||
             !string.Equals(owned.ServiceIdentity, service.ServiceIdentity, StringComparison.Ordinal)))
            throw new NetBirdManualRecoveryRequiredException("The current NetBird owner marker does not match the planned install.");

        var ready = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
        RequireSameObservedInstallation(current, ready);
        var process = await RunMutationAsync(
            () => _msi.UninstallExactAsync(plan.TargetPackage.Package.ProductCode, cancellationToken),
            "Interrupted NetBird uninstall outcome is uncertain and requires manual recovery.").ConfigureAwait(false);
        RequireSuccessfulMsi(process, "Interrupted NetBird uninstall did not return a proven-success exit code.");
        if (!await _state.VerifyPackageAbsentAsync(plan.TargetPackage.Package, CancellationToken.None).ConfigureAwait(false))
            throw new NetBirdManualRecoveryRequiredException("The exact interrupted NetBird install remains registered.");
        if (current.Assessment.OwnedState is { } ownedState)
            await _state.RemoveOwnershipAsync(ownedState, CancellationToken.None).ConfigureAwait(false);
        var after = await InspectCompleteAsync(CancellationToken.None).ConfigureAwait(false);
        if (after.Assessment.Ownership != NetBirdOwnership.Absent || after.InstalledPackage is not null)
            throw new NetBirdManualRecoveryRequiredException("Interrupted NetBird install cleanup was not proven absent.");
        return new(
            NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun,
            $"{current.EvidenceId}|{after.EvidenceId}");
    }

    private async ValueTask<NetBirdInterruptedRecoveryResult> ReconcileInterruptedUpdateAsync(
        NetBirdMutationPlan plan,
        NetBirdMachineInspection current,
        CancellationToken cancellationToken)
    {
        var restorePoint = plan.PriorRestorePoint!;
        if (IsExactPriorOwnedInstallation(current, restorePoint))
            return new(NetBirdInterruptedRecoveryAction.NoMutationObserved, current.EvidenceId);
        if (current.Assessment.Ownership == NetBirdOwnership.Foreign)
            throw new NetBirdManualRecoveryRequiredException("A foreign NetBird installation is present; recovery preserved it.");
        if (current.Assessment.Ownership != NetBirdOwnership.OwnedByConnector)
            throw new NetBirdManualRecoveryRequiredException("The interrupted NetBird update has no provable Connector owner.");
        if (!string.Equals(current.OwnerOperationId, plan.OperationId, StringComparison.Ordinal))
            throw new NetBirdManualRecoveryRequiredException(
                "The interrupted NetBird update is not causally bound to the durable operation id; recovery preserved it.");

        var targetService = await VerifyExactServiceAsync(plan.TargetPackage.Package, cancellationToken).ConfigureAwait(false);
        if (!IsExactPackage(current, plan.TargetPackage.Package, plan.TargetPackage.Sha256))
            throw new NetBirdManualRecoveryRequiredException("The current NetBird package is not the exact planned update.");
        var owned = current.Assessment.OwnedState!;
        var ownerMatchesPrior = owned == restorePoint.OwnedState;
        var ownerMatchesTarget =
            string.Equals(owned.Version, plan.TargetPackage.Package.ProductVersion, StringComparison.Ordinal) &&
            string.Equals(owned.ServiceIdentity, targetService.ServiceIdentity, StringComparison.Ordinal);
        if (!ownerMatchesPrior && !ownerMatchesTarget)
            throw new NetBirdManualRecoveryRequiredException("The NetBird owner marker cannot be tied to the interrupted update.");

        await using var restore = await _restorePoints.ReacquireAsync(restorePoint, cancellationToken).ConfigureAwait(false);
        ValidateRestoreLease(restore, restorePoint, expectedCurrent: null);
        var ready = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
        RequireSameObservedInstallation(current, ready);
        var process = await RunMutationAsync(
            () => _msi.InstallAsync(restore.StagedPath, cancellationToken),
            "Interrupted NetBird prior-state restore is uncertain and requires manual recovery.").ConfigureAwait(false);
        RequireSuccessfulMsi(process, "Interrupted NetBird prior-state restore did not return proven success.");
        var service = await VerifyExactServiceAsync(restore.Package, CancellationToken.None).ConfigureAwait(false);
        if (!string.Equals(service.ServiceIdentity, restorePoint.OwnedState.ServiceIdentity, StringComparison.Ordinal))
            throw new NetBirdManualRecoveryRequiredException("Restored NetBird service identity differs from the restore point.");
        await _state.RestoreOwnershipAsync(restore, service, CancellationToken.None).ConfigureAwait(false);
        var after = await InspectCompleteAsync(CancellationToken.None).ConfigureAwait(false);
        if (!IsExactPriorOwnedInstallation(after, restorePoint))
            throw new NetBirdManualRecoveryRequiredException("The exact prior Connector-owned NetBird state was not restored.");
        return new(
            NetBirdInterruptedRecoveryAction.RestoredPriorOwnedState,
            $"{current.EvidenceId}|{after.EvidenceId}");
    }

    public async ValueTask RemoveInstalledThisRunAsync(
        NetBirdInstalledThisRunReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        _elevation.AssertTrustedElevatedCaller();
        RequireOperationId(receipt.OperationId);
        ValidateOwnedState(receipt.InstalledState);

        var current = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
        RequireExactCurrentOwnedState(current, receipt.InstalledState,
            "Refusing to uninstall NetBird because the current installation is not the exact install from this run.");
        RequireOwnerOperationId(current, receipt.OperationId,
            "Refusing to uninstall NetBird because its live owner marker belongs to another operation.");
        var package = current.InstalledPackage!;
        var ready = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
        RequireSameObservedInstallation(current, ready);

        var process = await RunMutationAsync(
            () => _msi.UninstallExactAsync(package.ProductCode, cancellationToken),
            "NetBird uninstall outcome is uncertain and requires manual recovery.").ConfigureAwait(false);
        RequireSuccessfulMsi(process, "NetBird uninstall did not return a proven-success exit code.");

        try
        {
            if (!await _state.VerifyPackageAbsentAsync(package, CancellationToken.None).ConfigureAwait(false))
                throw new NetBirdManualRecoveryRequiredException(
                    "msiexec returned success but the exact NetBird package is still present.");
            await _state.RemoveOwnershipAsync(receipt.InstalledState, CancellationToken.None).ConfigureAwait(false);
            var after = await InspectCompleteAsync(CancellationToken.None).ConfigureAwait(false);
            if (after.Assessment.Ownership != NetBirdOwnership.Absent || after.InstalledPackage is not null)
                throw new NetBirdManualRecoveryRequiredException(
                    "The exact installed-this-run NetBird package was not proven absent after owner cleanup.");
        }
        catch (NetBirdManualRecoveryRequiredException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new NetBirdManualRecoveryRequiredException(
                "NetBird was mutated but final uninstall readback failed.", error);
        }
    }

    public async ValueTask RestoreUpdatedThisRunAsync(
        NetBirdUpdatedThisRunReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        _elevation.AssertTrustedElevatedCaller();
        RequireOperationId(receipt.OperationId);
        ValidateOwnedState(receipt.PriorOwnedState);
        ValidateOwnedState(receipt.UpdatedOwnedState);
        if (receipt.PriorRestorePoint.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            string.IsNullOrWhiteSpace(receipt.PriorRestorePoint.HandleId))
            throw new NetBirdMachineInvariantException("The update receipt has no protected prior-owned restore point.");

        var current = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
        RequireExactCurrentOwnedState(current, receipt.UpdatedOwnedState,
            "Refusing to restore NetBird because the current installation is not the exact update from this run.");
        RequireOwnerOperationId(current, receipt.OperationId,
            "Refusing to restore NetBird because its live owner marker belongs to another operation.");

        await using var restore = await _restorePoints
            .ReacquireAsync(receipt.PriorRestorePoint, cancellationToken)
            .ConfigureAwait(false);
        ValidateRestoreLease(restore, receipt.PriorRestorePoint, expectedCurrent: null);
        var ready = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
        RequireSameObservedInstallation(current, ready);

        var process = await RunMutationAsync(
            () => _msi.InstallAsync(restore.StagedPath, cancellationToken),
            "Prior NetBird restore outcome is uncertain and requires manual recovery.").ConfigureAwait(false);
        RequireSuccessfulMsi(process, "Prior NetBird restore did not return a proven-success exit code.");

        try
        {
            var service = await VerifyExactServiceAsync(restore.Package, CancellationToken.None).ConfigureAwait(false);
            if (!string.Equals(
                    service.ServiceIdentity,
                    receipt.PriorOwnedState.ServiceIdentity,
                    StringComparison.Ordinal))
                throw new NetBirdManualRecoveryRequiredException(
                    "The restored NetBird service identity does not match the protected prior-owned state.");
            await _state.RestoreOwnershipAsync(restore, service, CancellationToken.None).ConfigureAwait(false);
            var after = await InspectCompleteAsync(CancellationToken.None).ConfigureAwait(false);
            RequireExactCurrentOwnedState(after, receipt.PriorOwnedState,
                "The prior Connector-owned NetBird state was not restored exactly.");
            if (!IsExactPackage(after, restore.Package, restore.Sha256))
                throw new NetBirdManualRecoveryRequiredException(
                    "The restored NetBird package does not match the protected prior-owned payload.");
        }
        catch (NetBirdManualRecoveryRequiredException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new NetBirdManualRecoveryRequiredException(
                "NetBird was mutated but prior-owned state restoration failed.", error);
        }
    }

    private async ValueTask<NetBirdInstalledThisRunReceipt> InstallAbsentAsync(
        IVerifiedNetBirdPackageLease target,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var (service, rebootRequired) = await InstallAndPublishAsync(operationId, target, cancellationToken).ConfigureAwait(false);
        var after = await InspectCompleteAfterMutationAsync().ConfigureAwait(false);
        var state = RequireExactTargetOwned(after, target, service);
        return new NetBirdInstalledThisRunReceipt(operationId, state) { RebootRequired = rebootRequired };
    }

    private async ValueTask<NetBirdUpdatedThisRunReceipt> UpdateOwnedAsync(
        NetBirdMachineInspection before,
        IVerifiedNetBirdPackageLease target,
        CancellationToken cancellationToken)
    {
        await using var restore = await _restorePoints.CaptureAsync(before, cancellationToken).ConfigureAwait(false);
        ValidateRestoreLease(restore, restore.RestorePoint, before);
        var ready = await InspectCompleteAsync(cancellationToken).ConfigureAwait(false);
        RequireSameObservedInstallation(before, ready);

        var operationId = Guid.NewGuid().ToString("N");
        var (service, rebootRequired) = await InstallAndPublishAsync(operationId, target, cancellationToken).ConfigureAwait(false);
        var after = await InspectCompleteAfterMutationAsync().ConfigureAwait(false);
        var updated = RequireExactTargetOwned(after, target, service);
        return new NetBirdUpdatedThisRunReceipt(operationId, restore.RestorePoint, updated) { RebootRequired = rebootRequired };
    }

    private async ValueTask<(NetBirdInstalledServiceVerification Service, bool RebootRequired)> InstallAndPublishAsync(
        string operationId,
        IVerifiedNetBirdPackageLease target,
        CancellationToken cancellationToken)
    {
        var process = await RunMutationAsync(
            () => _msi.InstallAsync(target.StagedPath, cancellationToken),
            "NetBird install/update outcome is uncertain and requires manual recovery.").ConfigureAwait(false);
        RequireSuccessfulMsi(process, "NetBird install/update did not return a proven-success exit code.");

        try
        {
            var service = await VerifyExactServiceAsync(target.Package, CancellationToken.None).ConfigureAwait(false);
            await _state.PublishOwnershipAsync(operationId, target, service, CancellationToken.None).ConfigureAwait(false);
            return (service, process.ExitCode == 3010);
        }
        catch (NetBirdManualRecoveryRequiredException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new NetBirdManualRecoveryRequiredException(
                "NetBird was mutated but exact service readback or protected owner publication failed.", error);
        }
    }

    private async ValueTask<NetBirdInstalledServiceVerification> VerifyExactServiceAsync(
        NetBirdMsiPackageIdentity package,
        CancellationToken cancellationToken)
    {
        var verification = await _state
            .VerifyInstalledPackageAndServiceAsync(package, cancellationToken)
            .ConfigureAwait(false);
        if (!verification.IsExact || verification.Package != package ||
            string.IsNullOrWhiteSpace(verification.ServiceIdentity) ||
            string.IsNullOrWhiteSpace(verification.EvidenceId))
            throw new NetBirdManualRecoveryRequiredException(
                "The exact NetBird MSI registration and service identity were not proven after mutation.");
        return verification;
    }

    private async ValueTask<NetBirdMachineInspection> InspectCompleteAsync(CancellationToken cancellationToken)
    {
        var inspection = await _state.InspectAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new NetBirdMachineInvariantException("NetBird inventory returned no result.");
        ValidateInspection(inspection);
        return inspection;
    }

    private async ValueTask<NetBirdMachineInspection> InspectCompleteAfterMutationAsync()
    {
        try
        {
            return await InspectCompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not NetBirdManualRecoveryRequiredException)
        {
            throw new NetBirdManualRecoveryRequiredException(
                "NetBird was mutated but final ownership readback failed.", error);
        }
    }

    private static async ValueTask<WindowsMsiProcessResult> RunMutationAsync(
        Func<ValueTask<WindowsMsiProcessResult>> mutation,
        string uncertainMessage)
    {
        try
        {
            return await mutation().ConfigureAwait(false)
                ?? throw new NetBirdManualRecoveryRequiredException(uncertainMessage);
        }
        catch (NetBirdManualRecoveryRequiredException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new NetBirdManualRecoveryRequiredException(uncertainMessage, error);
        }
    }

    private static void RequireSuccessfulMsi(WindowsMsiProcessResult result, string message)
    {
        if (result.Completion != WindowsMsiProcessCompletion.Exited || result.ExitCode is not (0 or 3010))
            throw new NetBirdManualRecoveryRequiredException(message);
    }

    private void ValidatePlan(NetBirdMutationPlan plan)
    {
        ValidateRequestedAssessment(plan.PriorAssessment);
        ValidateTargetReceipt(plan.TargetPackage);
        var valid = (plan.PriorAssessment.Ownership, plan.Change) switch
        {
            (NetBirdOwnership.Absent, NetBirdChangeKind.InstalledThisRun) =>
                !string.IsNullOrWhiteSpace(plan.OperationId) && plan.PriorRestorePoint is null,
            (NetBirdOwnership.OwnedByConnector, NetBirdChangeKind.NoChange) =>
                plan.OperationId is null && plan.PriorRestorePoint is null,
            (NetBirdOwnership.OwnedByConnector, NetBirdChangeKind.UpdatedThisRun) =>
                !string.IsNullOrWhiteSpace(plan.OperationId) &&
                plan.PriorRestorePoint is not null &&
                plan.PriorRestorePoint.OwnedState == plan.PriorAssessment.OwnedState,
            _ => false,
        };
        if (!valid)
            throw new NetBirdMachineInvariantException("The NetBird mutation plan is inconsistent with its prior assessment.");
        if (plan.PriorRestorePoint is not null)
        {
            ValidateOwnedState(plan.PriorRestorePoint.OwnedState);
            if (plan.PriorRestorePoint.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
                string.IsNullOrWhiteSpace(plan.PriorRestorePoint.HandleId) ||
                plan.PriorRestorePoint.Package.ProductCode == Guid.Empty ||
                !IsSha256(plan.PriorRestorePoint.InstallerSha256))
                throw new NetBirdMachineInvariantException("The NetBird plan has no exact protected restore point.");
        }
    }

    private void ValidateTargetReceipt(ProtectedNetBirdPackageReceipt receipt)
    {
        if (receipt.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            string.IsNullOrWhiteSpace(receipt.HandleId) ||
            !string.Equals(receipt.InstallerName, _pin.InstallerName, StringComparison.Ordinal) ||
            receipt.SizeBytes != _pin.InstallerBytes ||
            !string.Equals(receipt.Sha256, _pin.InstallerSha256, StringComparison.OrdinalIgnoreCase) ||
            receipt.Package.ProductCode == Guid.Empty || receipt.Package.UpgradeCode == Guid.Empty ||
            !string.Equals(receipt.Package.ProductVersion, _pin.Version, StringComparison.Ordinal) ||
            !receipt.SignerSubject.StartsWith(_pin.SignerSubjectPrefix, StringComparison.Ordinal) ||
            !string.Equals(receipt.SignerThumbprint, _pin.SignerThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new NetBirdMachineInvariantException("The durable NetBird target does not match the official package lock.");
    }

    private static ProtectedNetBirdPackageReceipt ToProtectedReceipt(IVerifiedNetBirdPackageLease lease) => new(
        lease.HandleId,
        lease.Protection,
        Path.GetFileName(lease.StagedPath),
        lease.Package,
        lease.SizeBytes,
        lease.Sha256,
        lease.SignerSubject,
        lease.SignerThumbprint);

    private static void RequireTargetReceiptMatch(
        ProtectedNetBirdPackageReceipt receipt,
        IVerifiedNetBirdPackageLease lease)
    {
        if (ToProtectedReceipt(lease) != receipt)
            throw new NetBirdMachineInvariantException("Reacquired NetBird target does not match the durable plan.");
    }

    private static void RequireInspectionMatchesRestorePoint(
        NetBirdMachineInspection inspection,
        NetBirdOwnedRestorePoint restorePoint)
    {
        if (!IsExactPriorOwnedInstallation(inspection, restorePoint))
            throw new NetBirdMachineInvariantException("NetBird prior state no longer matches the prepared restore point.");
    }

    private static bool IsExactPriorOwnedInstallation(
        NetBirdMachineInspection inspection,
        NetBirdOwnedRestorePoint restorePoint) =>
        inspection.Assessment.Ownership == NetBirdOwnership.OwnedByConnector &&
        inspection.Assessment.OwnedState == restorePoint.OwnedState &&
        inspection.InstalledPackage == restorePoint.Package &&
        inspection.ServiceIdentityVerified &&
        string.Equals(inspection.InstallerSha256, restorePoint.InstallerSha256, StringComparison.OrdinalIgnoreCase);

    private void ValidateTargetLease(IVerifiedNetBirdPackageLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            string.IsNullOrWhiteSpace(lease.HandleId) ||
            string.IsNullOrWhiteSpace(lease.StagedPath) || !Path.IsPathFullyQualified(lease.StagedPath) ||
            !string.Equals(Path.GetFileName(lease.StagedPath), _pin.InstallerName, StringComparison.Ordinal) ||
            lease.SizeBytes != _pin.InstallerBytes ||
            !string.Equals(lease.Sha256, _pin.InstallerSha256, StringComparison.OrdinalIgnoreCase) ||
            !lease.AuthenticodeTrusted ||
            !lease.SignerSubject.StartsWith(_pin.SignerSubjectPrefix, StringComparison.Ordinal) ||
            !string.Equals(lease.SignerThumbprint, _pin.SignerThumbprint, StringComparison.OrdinalIgnoreCase) ||
            lease.Package.ProductCode == Guid.Empty || lease.Package.UpgradeCode == Guid.Empty ||
            !string.Equals(lease.Package.ProductVersion, _pin.Version, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(lease.Package.Manufacturer) ||
            string.IsNullOrWhiteSpace(lease.Package.ProductName))
            throw new NetBirdMachineInvariantException(
                "The protected NetBird MSI lease does not match the embedded official package lock.");
    }

    private static void ValidateRestoreLease(
        IVerifiedNetBirdRestorePointLease lease,
        NetBirdOwnedRestorePoint expectedReceipt,
        NetBirdMachineInspection? expectedCurrent)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.RestorePoint != expectedReceipt ||
            lease.RestorePoint.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            string.IsNullOrWhiteSpace(lease.RestorePoint.HandleId) ||
            string.IsNullOrWhiteSpace(lease.StagedPath) || !Path.IsPathFullyQualified(lease.StagedPath) ||
            lease.Package.ProductCode == Guid.Empty || lease.Package.UpgradeCode == Guid.Empty ||
            !lease.AuthenticodeTrusted || lease.SizeBytes <= 0 || !IsSha256(lease.Sha256) ||
            string.IsNullOrWhiteSpace(lease.SignerSubject) || string.IsNullOrWhiteSpace(lease.SignerThumbprint) ||
            lease.Package != lease.RestorePoint.Package ||
            !string.Equals(lease.Sha256, lease.RestorePoint.InstallerSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(lease.Package.ProductVersion, lease.RestorePoint.OwnedState.Version, StringComparison.Ordinal))
            throw new NetBirdMachineInvariantException("The prior-owned NetBird restore lease is not exact and protected.");

        if (expectedCurrent is not null &&
            (expectedCurrent.Assessment.OwnedState != lease.RestorePoint.OwnedState ||
             expectedCurrent.InstalledPackage != lease.Package ||
             !string.Equals(expectedCurrent.InstallerSha256, lease.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new NetBirdMachineInvariantException(
                "The captured restore point does not match the exact prior Connector-owned NetBird installation.");
    }

    private static NetBirdOwnedState RequireExactTargetOwned(
        NetBirdMachineInspection inspection,
        IVerifiedNetBirdPackageLease target,
        NetBirdInstalledServiceVerification service)
    {
        if (inspection.Assessment.Ownership != NetBirdOwnership.OwnedByConnector ||
            inspection.Assessment.OwnedState is null ||
            !IsExactPackage(inspection, target.Package, target.Sha256))
            throw new NetBirdManualRecoveryRequiredException(
                "The exact target NetBird package was not proven Connector-owned after mutation.");
        var owned = inspection.Assessment.OwnedState;
        ValidateOwnedState(owned);
        if (!string.Equals(owned.Version, target.Package.ProductVersion, StringComparison.Ordinal) ||
            !string.Equals(owned.ServiceIdentity, service.ServiceIdentity, StringComparison.Ordinal))
            throw new NetBirdManualRecoveryRequiredException(
                "The owned NetBird state reports the wrong target version or service identity.");
        return owned;
    }

    private static bool IsExactPackage(
        NetBirdMachineInspection inspection,
        NetBirdMsiPackageIdentity package,
        string sha256) =>
        inspection.ServiceIdentityVerified &&
        inspection.InstalledPackage == package &&
        string.Equals(inspection.InstallerSha256, sha256, StringComparison.OrdinalIgnoreCase);

    private static void RequireExactCurrentOwnedState(
        NetBirdMachineInspection inspection,
        NetBirdOwnedState exactState,
        string message)
    {
        RejectForeign(inspection);
        if (inspection.Assessment.Ownership != NetBirdOwnership.OwnedByConnector ||
            inspection.Assessment.OwnedState != exactState ||
            !string.Equals(inspection.Assessment.InstallationId, exactState.InstallationId, StringComparison.Ordinal) ||
            inspection.InstalledPackage is null || !inspection.ServiceIdentityVerified)
            throw new NetBirdMachineInvariantException(message);
    }

    private static void RequireOwnerOperationId(NetBirdMachineInspection inspection, string operationId, string message)
    {
        if (!string.Equals(inspection.OwnerOperationId, operationId, StringComparison.Ordinal))
            throw new NetBirdMachineInvariantException(message);
    }

    private static void ValidateInspection(NetBirdMachineInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection.Assessment);
        if (!inspection.InspectionComplete || string.IsNullOrWhiteSpace(inspection.EvidenceId))
            throw new NetBirdMachineInvariantException("NetBird inventory is incomplete; machine mutation is blocked.");

        switch (inspection.Assessment.Ownership)
        {
            case NetBirdOwnership.Absent when inspection.Assessment.InstallationId is null &&
                                                inspection.Assessment.OwnedState is null &&
                                                inspection.InstalledPackage is null:
                return;
            case NetBirdOwnership.OwnedByConnector when inspection.Assessment.OwnedState is not null &&
                                                          inspection.InstalledPackage is not null &&
                                                          inspection.ServiceIdentityVerified &&
                                                          IsSha256(inspection.InstallerSha256):
                ValidateOwnedState(inspection.Assessment.OwnedState);
                if (!string.Equals(
                        inspection.Assessment.InstallationId,
                        inspection.Assessment.OwnedState.InstallationId,
                        StringComparison.Ordinal))
                    throw new NetBirdMachineInvariantException("Owned NetBird installation ids disagree.");
                return;
            case NetBirdOwnership.Foreign:
                return;
            case NetBirdOwnership.Unattributed when inspection.Assessment.InstallationId is null &&
                                                     inspection.Assessment.OwnedState is null &&
                                                     inspection.InstalledPackage is not null &&
                                                     inspection.ServiceIdentityVerified &&
                                                     IsSha256(inspection.InstallerSha256):
                return;
            default:
                throw new NetBirdMachineInvariantException("NetBird inventory is internally inconsistent.");
        }
    }

    private static void ValidateRequestedAssessment(NetBirdAssessment assessment)
    {
        if (assessment.Ownership == NetBirdOwnership.Foreign)
            throw new NetBirdMachineInvariantException("Foreign NetBird installations are preserved and cannot be changed.");
        if (assessment.Ownership == NetBirdOwnership.Absent)
        {
            if (assessment.InstallationId is not null || assessment.OwnedState is not null)
                throw new NetBirdMachineInvariantException("Absent NetBird assessment contains installed state.");
            return;
        }
        if (assessment.Ownership == NetBirdOwnership.OwnedByConnector && assessment.OwnedState is not null)
        {
            ValidateOwnedState(assessment.OwnedState);
            if (string.Equals(assessment.InstallationId, assessment.OwnedState.InstallationId, StringComparison.Ordinal))
                return;
        }
        throw new NetBirdMachineInvariantException("The requested NetBird assessment is invalid.");
    }

    private static void RequireAssessmentMatch(NetBirdAssessment expected, NetBirdAssessment actual)
    {
        if (actual != expected)
            throw new NetBirdMachineInvariantException(
                "NetBird state changed after preflight; mutation is blocked until a new assessment is journaled.");
    }

    private static void RequireSameObservedInstallation(
        NetBirdMachineInspection expected,
        NetBirdMachineInspection actual)
    {
        RejectForeign(actual);
        if (expected.Assessment != actual.Assessment ||
            expected.InstalledPackage != actual.InstalledPackage ||
            !string.Equals(expected.InstallerSha256, actual.InstallerSha256, StringComparison.OrdinalIgnoreCase) ||
            expected.ServiceIdentityVerified != actual.ServiceIdentityVerified ||
            !string.Equals(expected.OwnerOperationId, actual.OwnerOperationId, StringComparison.Ordinal))
            throw new NetBirdMachineInvariantException(
                "NetBird machine state changed during protected staging; mutation is blocked.");
    }

    private static void RejectForeign(NetBirdMachineInspection inspection)
    {
        if (inspection.Assessment.Ownership == NetBirdOwnership.Foreign)
            throw new NetBirdMachineInvariantException("Foreign NetBird installation was detected and preserved.");
    }

    private static void RejectForeignOrUnattributed(NetBirdMachineInspection inspection)
    {
        if (inspection.Assessment.Ownership is NetBirdOwnership.Foreign or NetBirdOwnership.Unattributed)
            throw new NetBirdMachineInvariantException("Unowned NetBird installation was detected and preserved.");
    }

    private static void ValidateOwnedState(NetBirdOwnedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.InstallationId) ||
            string.IsNullOrWhiteSpace(state.Version) ||
            string.IsNullOrWhiteSpace(state.ServiceIdentity) ||
            !IsSha256(state.ConfigurationSha256))
            throw new NetBirdMachineInvariantException("Connector-owned NetBird state is incomplete.");
    }

    private static bool IsSha256(string? value)
    {
        if (value?.Length != 64 || !value.All(Uri.IsHexDigit))
            return false;
        return true;
    }

    private static void RequireOperationId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new NetBirdMachineInvariantException("A non-empty NetBird operation id is required.");
    }
}
