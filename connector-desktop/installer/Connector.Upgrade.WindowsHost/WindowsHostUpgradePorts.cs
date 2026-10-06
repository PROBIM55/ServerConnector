using System.Security.Principal;
using System.Collections.Concurrent;
using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;
using Connector.Upgrade.UserState;
using Connector.Upgrade.Velopack;
using Connector.Upgrade.VelopackPayload;
using Connector.Upgrade.WindowsMsi;
using Connector.Upgrade.WindowsPayload;

namespace Connector.Upgrade.WindowsHost;

public sealed record WindowsHostProductionOptions(
    WindowsHostSession Session,
    WindowsRollbackPayloadSources LegacyRollbackSources,
    string NetBirdSourceMsiPath,
    VelopackSetupPin VelopackSetupPin,
    TimeSpan MutationTimeout);

/// <summary>One-PC Windows composition of the accepted upgrade modules.</summary>
public sealed class WindowsHostUpgradePorts : IUpgradePorts, IUpgradeSessionBoundPorts,
    IOriginalUserOperationRollbackPayloadOpener
{
    private static readonly LegacyApplicationKind[] LegacyOrder =
    [
        LegacyApplicationKind.StructuraConnector,
        LegacyApplicationKind.PlatformConnector,
    ];

    private readonly WindowsHostSession _session;
    private readonly IWindowsHostEnvironmentPort _environment;
    private readonly WindowsMsiMutationAdapter _msi;
    private readonly IWindowsRollbackPayloadStore _rollbackPayloads;
    private readonly IWindowsHostUserStatePort _userState;
    private readonly IWindowsHostNetBirdPort _netBird;
    // Legacy in-process path only. Restore data is machine-owned; after a restart this cache is
    // empty and recovery fails closed until the machine-command adapter is composed here.
    private readonly ConcurrentDictionary<string, NetBirdMutationPlan> _netBirdPlans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NetBirdMutationReceipt> _netBirdReceipts = new(StringComparer.Ordinal);
    private readonly IWindowsHostUnifiedApplicationPort _unified;
    private readonly IPlatformEnrollmentPort _enrollment;
    private readonly ILiveNewAccessVerificationPort _access;
    private readonly LegacyUpgradeLock _upgradeLock;
    private readonly ConcurrentDictionary<LegacyApplicationKind, IWindowsVerifiedRollbackPayloadLease> _liveRollbackLeases = new();
    private UnifiedApplicationReceipt? _unifiedReceipt;
    private PlatformEnrollmentReceipt? _enrollmentReceipt;

    public WindowsHostUpgradePorts(
        WindowsHostSession session,
        IWindowsHostEnvironmentPort environment,
        WindowsMsiMutationAdapter msi,
        IWindowsRollbackPayloadStore rollbackPayloads,
        IWindowsHostUserStatePort userState,
        IWindowsHostNetBirdPort netBird,
        IWindowsHostUnifiedApplicationPort unified,
        IPlatformEnrollmentPort enrollment,
        ILiveNewAccessVerificationPort access,
        LegacyUpgradeLock? upgradeLock = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.Validate();
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _msi = msi ?? throw new ArgumentNullException(nameof(msi));
        _rollbackPayloads = rollbackPayloads ?? throw new ArgumentNullException(nameof(rollbackPayloads));
        _userState = userState ?? throw new ArgumentNullException(nameof(userState));
        _netBird = netBird ?? throw new ArgumentNullException(nameof(netBird));
        _unified = unified ?? throw new ArgumentNullException(nameof(unified));
        _enrollment = enrollment ?? throw new ArgumentNullException(nameof(enrollment));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _upgradeLock = upgradeLock ?? LegacyUpgradeLock.LoadEmbedded();
        AssertSameUser();
    }

    /// <summary>Requires a caller's original-user context to match this ports instance exactly.</summary>
    public void AssertSessionMatches(WindowsHostSession expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        expected.Validate();
        AssertSessionMatches(expected.OperationId, expected.InitiatingUserSid);
    }

    public void AssertSessionMatches(string operationId, string initiatingUserSid)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(initiatingUserSid) ||
            !StringComparer.Ordinal.Equals(_session.OperationId, operationId) ||
            !StringComparer.OrdinalIgnoreCase.Equals(_session.InitiatingUserSid, initiatingUserSid))
            throw new UnauthorizedAccessException("The Windows host ports belong to a different upgrade session.");
    }

    public static WindowsHostUpgradePorts CreateForCurrentUser(
        WindowsHostProductionOptions options,
        IWindowsHostEnvironmentPort environment,
        IPlatformEnrollmentPort enrollment,
        ILiveNewAccessVerificationPort access,
        IVerifiedNetBirdPackageStager netBirdPackageStager,
        INetBirdMachineStatePort netBirdState,
        INetBirdOwnedRestorePointStore netBirdRestorePoints,
        IWindowsVelopackSetupStager velopackSetupStager)
    {
        throw new NotSupportedException(
            "The elevated current-user composition is a fail-closed prototype. Use CreateForOriginalUser with authenticated one-shot machine-helper ports.");
    }

    /// <summary>
    /// Composes the host while running as the initiating user. Machine-scope NetBird work and the
    /// protected Setup lease are supplied by their authenticated/pre-staged adapters; this method
    /// never creates an elevated caller, machine service, protected stage, or journal.
    /// </summary>
    public static WindowsHostUpgradePorts CreateForOriginalUser(
        WindowsHostProductionOptions options,
        IWindowsHostEnvironmentPort environment,
        IPlatformEnrollmentPort enrollment,
        ILiveNewAccessVerificationPort access,
        IWindowsHostNetBirdPort netBird,
        IWindowsVelopackSetupStager velopackSetupStager,
        IWindowsHostUserStatePort userState,
        IWindowsRollbackPayloadStore? rollbackPayloads = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows upgrade host is available only on Windows.");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");
        if (!string.Equals(sid, options.Session.InitiatingUserSid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The current Windows user is not the initiating user.");

        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(netBird);
        ArgumentNullException.ThrowIfNull(velopackSetupStager);
        ArgumentNullException.ThrowIfNull(userState);

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var unified = new VelopackUnifiedApplicationPort(
            velopackSetupStager,
            options.VelopackSetupPin,
            new SystemVelopackInstallProbe(sid, localApplicationData),
            new SystemVelopackProcessRunner(),
            sid,
            localApplicationData,
            options.MutationTimeout);
        return new WindowsHostUpgradePorts(
            options.Session,
            environment,
            WindowsMsiMutationAdapter.CreateForCurrentUser(
                options.MutationTimeout,
                async (_, cancellationToken) =>
                {
                    foreach (var kind in LegacyOrder)
                        await environment.DrainLegacyApplicationAsync(kind, cancellationToken)
                            .ConfigureAwait(false);
                }),
            rollbackPayloads ?? new WindowsOriginalUserRollbackPayloadStore(
                Guid.ParseExact(options.Session.OperationId, "N"),
                new WindowsOperationRollbackPayloadOpener(
                    Guid.ParseExact(options.Session.OperationId, "N"), options.Session.InitiatingUserSid)),
            userState,
            netBird,
            unified,
            enrollment,
            access);
    }

    public async ValueTask<UpgradePreflight> InspectAsync(CancellationToken cancellationToken)
    {
        AssertSameUser();
        var readiness = await _environment.InspectReadinessAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(readiness.EvidenceId))
            throw new UpgradeInvariantException("Windows host readiness has no evidence identity.");

        var installed = new List<InstalledLegacyApplication>(LegacyOrder.Length);
        var presenceBaseline = new List<LegacyApplicationPresenceBaseline>(LegacyOrder.Length);
        foreach (var kind in LegacyOrder)
        {
            var exact = _msi.InspectExact(kind);
            var pin = _upgradeLock.Get(kind);
            if (exact.Identity != pin.Identity)
                throw new UpgradeInvariantException($"The {kind} MSI inspection is not bound to the locked product identity.");

            switch (exact.Presence)
            {
                case ExactWindowsMsiPresence.ExactInstalled when exact.Registration is not null:
                    installed.Add(new InstalledLegacyApplication(exact.Identity, exact.Version, ExactMsiIdentityVerified: true));
                    presenceBaseline.Add(new LegacyApplicationPresenceBaseline(
                        exact.Identity,
                        ExactLegacyPresence.ExactInstalled,
                        $"windows-msi:{_session.OperationId}:{kind}:installed:{Guid.NewGuid():N}",
                        exact.Version));
                    break;
                case ExactWindowsMsiPresence.Absent when exact.Registration is null:
                    presenceBaseline.Add(new LegacyApplicationPresenceBaseline(
                        exact.Identity,
                        ExactLegacyPresence.Absent,
                        $"windows-msi:{_session.OperationId}:{kind}:absent:{Guid.NewGuid():N}",
                        InstalledVersion: null));
                    break;
                default:
                    throw new UpgradeInvariantException($"The {kind} MSI state is not an exact installed or verified absent result.");
            }
        }

        var snapshot = await _userState
            .CaptureAsync(_session.InitiatingUserSid, _session.OperationId, cancellationToken)
            .ConfigureAwait(false);
        var netBird = await _netBird.InspectAsync(cancellationToken).ConfigureAwait(false);
        var rollbackPayloads = presenceBaseline
            .Where(item => item.Presence == ExactLegacyPresence.ExactInstalled)
            .Select(item => item.Identity.Kind)
            .Select(kind => ToRollbackPayload(_upgradeLock.Get(kind)))
            .ToArray();
        return new UpgradePreflight(
            _session.InitiatingUserSid,
            installed,
            rollbackPayloads,
            snapshot,
            netBird,
            readiness.NewServerSchemaAvailable,
            readiness.LegacyWorkCanDrainSafely,
            presenceBaseline);
    }

    public async ValueTask<NetBirdOperationIntent> PrepareOwnedNetBirdMutationAsync(
        NetBirdAssessment assessment,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        var plan = await _netBird.PrepareAsync(
            assessment,
            Guid.ParseExact(_session.OperationId, "N"),
            cancellationToken).ConfigureAwait(false);
        if (!_netBirdPlans.TryAdd(_session.OperationId, plan))
            throw new WindowsHostManualRecoveryRequiredException("NetBird operation id was already prepared in this process.");
        return new NetBirdOperationIntent(
            _session.OperationId, assessment.Ownership, plan.Change, NetBirdOperationStatus.Prepared);
    }

    public async ValueTask<NetBirdOperationReceipt> ApplyOwnedNetBirdMutationAsync(
        NetBirdOperationIntent operation,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        if (!string.Equals(operation.OperationId, _session.OperationId, StringComparison.Ordinal) ||
            !_netBirdPlans.TryGetValue(operation.OperationId, out var plan))
            throw new WindowsHostManualRecoveryRequiredException(
                "The machine command adapter is required to apply this persisted NetBird operation.");
        if (operation.Status != NetBirdOperationStatus.Prepared || operation.Change != plan.Change ||
            operation.PriorOwnership != plan.PriorAssessment.Ownership)
            throw new WindowsHostManualRecoveryRequiredException("NetBird operation intent differs from the prepared plan.");

        var richReceipt = await _netBird.ApplyAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!_netBirdReceipts.TryAdd(operation.OperationId, richReceipt))
            throw new WindowsHostManualRecoveryRequiredException("NetBird operation receipt was already applied in this process.");
        return ToOpaqueReceipt(operation, richReceipt);
    }

    public async ValueTask CompensateNetBirdMutationAsync(
        NetBirdOperationIntent operation,
        NetBirdOperationReceipt receipt,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        if (!string.Equals(operation.OperationId, receipt.OperationId, StringComparison.Ordinal) ||
            !_netBirdReceipts.TryGetValue(operation.OperationId, out var richReceipt))
            throw new WindowsHostManualRecoveryRequiredException(
                "The machine command adapter is required to compensate this persisted NetBird operation.");
        switch (richReceipt)
        {
            case NetBirdInstalledThisRunReceipt installed:
                await _netBird.RemoveInstalledThisRunAsync(installed, cancellationToken).ConfigureAwait(false);
                break;
            case NetBirdUpdatedThisRunReceipt updated:
                await _netBird.RestoreUpdatedThisRunAsync(updated, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new WindowsHostManualRecoveryRequiredException("NoChange NetBird operations have no compensation.");
        }
    }

    public async ValueTask<UnifiedApplicationReceipt> InstallUnifiedApplicationAsync(
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        var receipt = await _unified.InstallAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(receipt.OperationId) || _unified.GetRecoveryMetadata(receipt) is null)
            throw new WindowsHostManualRecoveryRequiredException("Velopack returned no typed recovery metadata.");
        _unifiedReceipt = receipt;
        return receipt;
    }

    public async ValueTask RemoveUnifiedApplicationAsync(
        UnifiedApplicationReceipt receipt,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        await _unified.RemoveAsync(receipt, cancellationToken).ConfigureAwait(false);
        if (_unifiedReceipt == receipt) _unifiedReceipt = null;
    }

    public async ValueTask<PlatformEnrollmentReceipt> EnrollAsync(
        OneTimePlatformToken token,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        var receipt = await _enrollment.EnrollAsync(token, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(receipt.EnrollmentId))
            throw new UpgradeInvariantException("Platform enrollment returned no exact identity.");
        _enrollmentReceipt = receipt;
        return receipt;
    }

    public async ValueTask RemoveNewEnrollmentAsync(
        PlatformEnrollmentReceipt receipt,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        await _enrollment.RemoveAsync(receipt, cancellationToken).ConfigureAwait(false);
        if (_enrollmentReceipt == receipt) _enrollmentReceipt = null;
    }

    public ValueTask<NewAccessVerification> VerifyNewAccessAsync(
        PlatformEnrollmentReceipt receipt,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        return _access.VerifyAsync(receipt, cancellationToken);
    }

    public async ValueTask<IVerifiedRollbackPayloadLease> AcquireVerifiedRollbackPayloadAsync(
        LegacyRollbackPayload payload,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        var lease = await _rollbackPayloads
            .AcquireVerifiedRollbackPayloadAsync(payload, cancellationToken)
            .ConfigureAwait(false);
        return await TrackLiveRollbackLeaseAsync(lease, payload.Identity.Kind).ConfigureAwait(false);
    }

    public async ValueTask<IVerifiedRollbackPayloadLease> OpenExistingRollbackPayloadAsync(
        ProtectedRollbackPayloadReceipt receipt,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        ArgumentNullException.ThrowIfNull(receipt);
        if (_rollbackPayloads is not WindowsOriginalUserRollbackPayloadStore)
            throw new UnauthorizedAccessException("The Windows host has no operation-scoped original-user rollback opener.");
        var lease = await _rollbackPayloads.ReacquireVerifiedRollbackPayloadAsync(receipt, cancellationToken)
            .ConfigureAwait(false);
        return await TrackLiveRollbackLeaseAsync(lease, receipt.Kind).ConfigureAwait(false);
    }

    private async ValueTask<IVerifiedRollbackPayloadLease> TrackLiveRollbackLeaseAsync(
        IVerifiedRollbackPayloadLease lease, LegacyApplicationKind kind)
    {
        if (lease is not IWindowsVerifiedRollbackPayloadLease windowsLease ||
            windowsLease.Inspection.Identity.Kind != kind)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw new UpgradeInvariantException("Windows MSI removal requires a live Windows rollback payload lease.");
        }
        var tracked = new TrackedRollbackPayloadLease(this, kind, windowsLease);
        if (!_liveRollbackLeases.TryAdd(kind, tracked))
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw new UpgradeInvariantException($"A rollback lease already exists for {kind}.");
        }
        return tracked;
    }

    public async ValueTask<LegacyRemovalReceipt> RemoveExactLegacyApplicationAsync(
        LegacyApplicationIdentity identity,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        if (!_liveRollbackLeases.TryGetValue(identity.Kind, out var lease) || lease.Inspection.Identity != identity)
            throw new UpgradeInvariantException("The exact live rollback lease is missing before legacy removal.");
        var drain = await _environment
            .DrainLegacyApplicationAsync(identity.Kind, cancellationToken)
            .ConfigureAwait(false);
        var result = await _msi
            .RemoveExactAsync(identity.Kind, drain, lease, cancellationToken)
            .ConfigureAwait(false);
        RequireMsiMutation(result, ExactWindowsMsiPresence.Absent, allowAlready: false);
        return new LegacyRemovalReceipt(
            identity,
            Guid.NewGuid().ToString("N"),
            result.Disposition == WindowsMsiMutationDisposition.SucceededRebootRequired);
    }

    public async ValueTask RestoreExactLegacyApplicationAsync(
        IVerifiedRollbackPayloadLease payloadLease,
        LegacyRemovalReceipt removal,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        if (payloadLease is not IWindowsVerifiedRollbackPayloadLease windowsLease ||
            windowsLease.Inspection.Identity != removal.Identity)
            throw new UpgradeInvariantException("Legacy restore does not have the exact Windows rollback lease.");
        var result = await _msi
            .RestoreExactAsync(removal.Identity.Kind, windowsLease, cancellationToken)
            .ConfigureAwait(false);
        RequireMsiMutation(result, ExactWindowsMsiPresence.ExactInstalled, allowAlready: true);
    }

    public ValueTask RestoreUserStateAsync(UserStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        AssertSameUser();
        return _userState.RestoreAsync(snapshot, cancellationToken);
    }

    public async ValueTask<FinalUpgradeVerification> VerifyFinalStateAsync(CancellationToken cancellationToken)
    {
        AssertSameUser();
        var unifiedReady = _unifiedReceipt is not null &&
                           await _unified.IsExactInstallReadyAsync(cancellationToken).ConfigureAwait(false);
        var access = _enrollmentReceipt is null
            ? new NewAccessVerification(false, false, false)
            : await _access.VerifyAsync(_enrollmentReceipt, cancellationToken).ConfigureAwait(false);
        var legacyAbsent = LegacyOrder.All(kind =>
            _msi.InspectExact(kind).Presence == ExactWindowsMsiPresence.Absent);
        return new FinalUpgradeVerification(unifiedReady, access.IsSuccessful, legacyAbsent);
    }

    public async ValueTask<InterruptedUpgradeInspection> InspectInterruptedUpgradeAsync(
        UpgradeRecoveryMetadata recovery,
        CancellationToken cancellationToken)
    {
        AssertRecoveryUser(recovery);
        var legacy = LegacyOrder.Select(kind =>
        {
            var exact = _msi.InspectExact(kind);
            return new LegacyRecoveryObservation(
                exact.Identity,
                exact.Presence == ExactWindowsMsiPresence.ExactInstalled
                    ? ExactLegacyPresence.ExactInstalled
                    : ExactLegacyPresence.Absent,
                $"windows-msi:{exact.Identity.ProductCode:D}:{exact.Presence}");
        }).ToArray();
        var user = await _userState
            .InspectRecoveryAsync(recovery.Preflight.UserState, cancellationToken)
            .ConfigureAwait(false);
        return new InterruptedUpgradeInspection(
            _session.InitiatingUserSid,
            legacy,
            user.CanRestore,
            user.MatchesSnapshot);
    }

    public ValueTask<IVerifiedRollbackPayloadLease> ReacquireVerifiedRollbackPayloadAsync(
        ProtectedRollbackPayloadReceipt durableReceipt,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        return _rollbackPayloads.ReacquireVerifiedRollbackPayloadAsync(durableReceipt, cancellationToken);
    }

    public async ValueTask RestoreMissingLegacyApplicationAsync(
        IVerifiedRollbackPayloadLease payloadLease,
        LegacyRecoveryObservation absentApplication,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        if (absentApplication.Presence != ExactLegacyPresence.Absent ||
            payloadLease is not IWindowsVerifiedRollbackPayloadLease windowsLease ||
            windowsLease.Inspection.Identity != absentApplication.Identity)
            throw new UpgradeInvariantException("Interrupted restore is not bound to one exact absent legacy MSI.");
        var result = await _msi
            .RestoreExactAsync(absentApplication.Identity.Kind, windowsLease, cancellationToken)
            .ConfigureAwait(false);
        RequireMsiMutation(result, ExactWindowsMsiPresence.ExactInstalled, allowAlready: false);
    }

    public ValueTask<NetBirdInterruptedRecoveryResult> ReconcileInterruptedNetBirdAsync(
        NetBirdOperationIntent operation,
        CancellationToken cancellationToken)
    {
        AssertSameUser();
        if (!_netBirdPlans.TryGetValue(operation.OperationId, out var plan))
            throw new WindowsHostManualRecoveryRequiredException(
                "The machine command adapter is required to reconcile this persisted NetBird operation.");
        if (operation.Status != NetBirdOperationStatus.Prepared || plan.Change != operation.Change ||
            plan.PriorAssessment.Ownership != operation.PriorOwnership)
            throw new WindowsHostManualRecoveryRequiredException("NetBird recovery intent differs from the prepared plan.");
        return _netBird.ReconcileAsync(plan, cancellationToken);
    }

    private static NetBirdOperationReceipt ToOpaqueReceipt(
        NetBirdOperationIntent operation,
        NetBirdMutationReceipt receipt) => receipt switch
    {
        NetBirdNoChangeReceipt noChange => new(
            operation.OperationId, operation.PriorOwnership, receipt.Change, NetBirdOperationStatus.Applied,
            NetBirdOwnership.OwnedByConnector),
        NetBirdInstalledThisRunReceipt installed => new(
            installed.OperationId, operation.PriorOwnership, receipt.Change, NetBirdOperationStatus.Applied,
            NetBirdOwnership.OwnedByConnector, installed.RebootRequired),
        NetBirdUpdatedThisRunReceipt updated => new(
            updated.OperationId, operation.PriorOwnership, receipt.Change, NetBirdOperationStatus.Applied,
            NetBirdOwnership.OwnedByConnector, updated.RebootRequired),
        _ => throw new WindowsHostManualRecoveryRequiredException("The machine returned an unsupported NetBird receipt."),
    };

    private void AssertRecoveryUser(UpgradeRecoveryMetadata recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        AssertSameUser();
        if (!string.Equals(
                recovery.Preflight.CurrentWindowsUserId,
                _session.InitiatingUserSid,
                StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Interrupted upgrade belongs to a different Windows SID.");
    }

    private void AssertSameUser()
    {
        var current = _environment.GetCurrentUserSid();
        if (string.IsNullOrWhiteSpace(current) ||
            !string.Equals(current, _session.InitiatingUserSid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The current Windows SID differs from the initiating user.");
    }

    private static LegacyRollbackPayload ToRollbackPayload(LegacyUpgradePin pin) => new(
        pin.Identity,
        pin.PackageId,
        pin.InstallerName,
        pin.Version,
        pin.SizeBytes,
        pin.Sha256,
        Verified: true);

    private static void RequireMsiMutation(
        WindowsMsiMutationResult result,
        ExactWindowsMsiPresence expected,
        bool allowAlready)
    {
        var dispositionOk = result.Disposition is WindowsMsiMutationDisposition.Succeeded or
            WindowsMsiMutationDisposition.SucceededRebootRequired ||
            allowAlready && result.Disposition == WindowsMsiMutationDisposition.AlreadyInDesiredState;
        if (!dispositionOk || result.StatusProbe?.Presence != expected)
            throw new WindowsHostManualRecoveryRequiredException(
                $"Exact MSI mutation for {result.Identity.Kind} was not proven: {result.Detail}");
    }

    private sealed class TrackedRollbackPayloadLease(
        WindowsHostUpgradePorts owner,
        LegacyApplicationKind kind,
        IWindowsVerifiedRollbackPayloadLease inner) : IWindowsVerifiedRollbackPayloadLease
    {
        private int _disposed;
        public string HandleId => inner.HandleId;
        public RollbackPayloadProtection Protection => inner.Protection;
        public RollbackPayloadInspection Inspection => inner.Inspection;
        public string StagedPath => inner.StagedPath;
        public Microsoft.Win32.SafeHandles.SafeFileHandle ContentHandle => inner.ContentHandle;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Do not admit another lease until the underlying pinned file handle is closed.
            await inner.DisposeAsync().ConfigureAwait(false);
            owner._liveRollbackLeases.TryRemove(kind, out _);
        }
    }
}
