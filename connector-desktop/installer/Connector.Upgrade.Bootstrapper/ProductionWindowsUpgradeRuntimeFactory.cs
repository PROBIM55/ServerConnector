using System.Security.Principal;
using Connector.CommonAccess.Runtime;
using Connector.Upgrade.Core;
using Connector.Upgrade.HelperReleaseTrust;
using Connector.Upgrade.MachineCommandChannel;
using Connector.Upgrade.MachineDispatcher;
using Connector.Upgrade.MachineIpc;
using Connector.Upgrade.NetBirdMachine;
using Connector.Upgrade.OriginalUserCaller;
using Connector.Upgrade.Velopack;
using Connector.Upgrade.VelopackPayload;
using Connector.Upgrade.WindowsComposition;
using Connector.Upgrade.WindowsHost;
using Connector.Upgrade.WindowsPayload;
using Connector.Upgrade.WindowsUserJournal;

namespace Connector.Upgrade.Bootstrapper;

internal readonly record struct BootstrapperOperationResolution(
    Guid OperationId, bool HasJournal, Guid? PreviousOperationId = null, bool NeedsMachineRollover = false);

/// <summary>
/// Original-user entrypoint. Only the machine-controlled deployment and protected release manifest
/// supply endpoints, asset names and hashes; no CLI, environment override or per-user config does.
/// </summary>
public sealed class ProductionWindowsUpgradeRuntimeFactory : IWindowsUpgradeBootstrapperRuntimeFactory
{
    public async ValueTask<BootstrapperPreparation> PrepareAsync(BootstrapperAction action, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return Blocked("WindowsHostRequired");
        IDisposable? runGuard = null;
        var guardTransferred = false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var sid = identity.User?.Value;
            if (string.IsNullOrWhiteSpace(sid)) return Blocked("WindowsHostRequired");

            var localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Structura Connector", "Agent");
            var deployment = CommonAccessDeploymentFactory.OpenProduction(localRoot);
            if (!deployment.IsConfigured || deployment.ControlPlaneBaseUri is null ||
                deployment.NetBirdManagementUri is null)
                return Blocked("shared-common-access-runtime-factory-unavailable");

            var (operationId, hasJournal, acquiredGuard) = await ReadOrCreateOperationIdAsync(sid, action, cancellationToken).ConfigureAwait(false);
            runGuard = acquiredGuard;
            if (hasJournal && action == BootstrapperAction.ExecuteUpgrade)
            {
                guardTransferred = true;
                return Blocked("ExistingOperationRequiresRecovery") with { RunGuard = runGuard };
            }
            if (!hasJournal && action == BootstrapperAction.RecoverInterrupted)
            {
                guardTransferred = true;
                return Blocked("RecoveryJournalMissing") with { RunGuard = runGuard };
            }
            var releaseSetup = new HelperReleasePinSource().GetVelopackSetupPin();
            if (!string.Equals(releaseSetup.PackageId, UnifiedVelopackApplication.PackId, StringComparison.Ordinal))
                return Blocked("SignedReleasePinUnavailable");
            var trustMode = releaseSetup.TrustMode switch
            {
                Connector.Upgrade.HelperLauncher.ReleaseArtifactTrustMode.Authenticode => VelopackSetupTrustMode.Authenticode,
                Connector.Upgrade.HelperLauncher.ReleaseArtifactTrustMode.SignedManifestHash => VelopackSetupTrustMode.SignedManifestHash,
                _ => throw new InvalidDataException("Unsupported Setup trust mode.")
            };
            var setupPin = new Connector.Upgrade.Velopack.VelopackSetupPin(releaseSetup.PackageId,
                releaseSetup.Version, releaseSetup.Size, releaseSetup.Sha256,
                trustMode, releaseSetup.SignerThumbprint);

            var helperRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "StructuraConnectorInstaller", "Helper");
            var legacy = LegacyUpgradeLock.LoadEmbedded();
            var rollback = new WindowsRollbackPayloadSources(
                Path.Combine(helperRoot, legacy.Get(LegacyApplicationKind.StructuraConnector).InstallerName),
                Path.Combine(helperRoot, legacy.Get(LegacyApplicationKind.PlatformConnector).InstallerName));
            var netBird = Path.Combine(helperRoot, OfficialNetBirdPackagePin.LoadEmbedded().InstallerName);
            var host = new WindowsHostProductionOptions(
                new WindowsHostSession(operationId.ToString("N"), sid), rollback, netBird,
                setupPin, TimeSpan.FromMinutes(10));
            var profileUri = new Uri(deployment.ControlPlaneBaseUri.AbsoluteUri.TrimEnd('/') + "/profile");
            var allowedSigner = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(releaseSetup.SignerThumbprint))
                allowedSigner.Add(releaseSetup.SignerThumbprint);
            var options = new WindowsUpgradeCompositionOptions(host, profileUri,
                deployment.NetBirdManagementUri, Environment.MachineName,
                new VelopackSetupSource(releaseSetup.AbsolutePath),
                new VelopackSetupSignaturePolicy(allowedSigner));
            var bindings = new WindowsUpgradeRuntimeBindings(deployment.EnrollmentClient!,
                deployment.OverlayClient!, deployment.ProtectedNetworkGate!,
                deployment.CreateRequestTransport(), deployment.SmbDestinations);
            var preparation = await new WindowsCompositionRuntimeAdapter(token =>
                WindowsUpgradeCompositionFactory.PrepareForCurrentUserAsync(options, bindings, token))
                .PrepareAsync(action, cancellationToken).ConfigureAwait(false);
            guardTransferred = true;
            return preparation with { RunGuard = runGuard };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // No exception text reaches the UI: configuration and network failures can carry secrets.
            return Blocked("MachineBoundaryNotReady");
        }
        finally
        {
            if (!guardTransferred) runGuard?.Dispose();
        }
    }

    private static async ValueTask<(Guid RunId, bool HasJournal, IDisposable Guard)> ReadOrCreateOperationIdAsync(
        string sid, BootstrapperAction action, CancellationToken cancellationToken)
    {
        using var store = new WindowsUserUpgradeJournalStore(sid);
        var guard = store.AcquireBootstrapperRunGuard();
        try
        {
            Guid operationId;
            Guid? previousOperationId = null;
            BootstrapperOperationResolution resolution;
            await using (var lease = await store.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false))
            {
                var existing = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                var rollover = await store.LoadRolloverAsync(cancellationToken).ConfigureAwait(false);
                resolution = ResolveExistingOperation(Guid.NewGuid(), existing, rollover, action);
                operationId = resolution.OperationId;
                previousOperationId = resolution.PreviousOperationId;
                if (resolution.NeedsMachineRollover && existing?.RunId == resolution.PreviousOperationId)
                    await store.PrepareRolloverAsync(resolution.PreviousOperationId!.Value, operationId, cancellationToken).ConfigureAwait(false);
            }

            if (resolution.NeedsMachineRollover)
            {
                await RolloverMachineJournalAsync(previousOperationId!.Value, operationId, cancellationToken).ConfigureAwait(false);
                await using var commitLease = await store.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
                await store.CommitRolloverAsync(previousOperationId.Value, operationId, cancellationToken).ConfigureAwait(false);
                return (operationId, resolution.HasJournal, guard);
            }

            return (operationId, resolution.HasJournal, guard);
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    internal static BootstrapperOperationResolution ResolveExistingOperation(
        Guid candidateOperationId, UpgradeJournalDocument? existing, UserJournalRolloverRecord? rollover,
        BootstrapperAction action)
    {
        if (candidateOperationId == Guid.Empty) throw new ArgumentException("A candidate operation id is required.", nameof(candidateOperationId));
        if (existing is null)
        {
            if (rollover is not null) throw new InvalidDataException("A rollover record exists without its active user journal.");
            return new(candidateOperationId, false);
        }
        if (existing.RunId == Guid.Empty || existing.SchemaVersion != UpgradeJournalDocument.CurrentSchemaVersion)
            throw new InvalidDataException("Existing upgrade journal cannot be resumed automatically.");

        if (rollover is { State: UserJournalRolloverState.Prepared })
        {
            if (existing.RunId != rollover.PreviousRunId && existing.RunId != rollover.NextRunId)
                throw new InvalidDataException("The current journal does not match the prepared rollover.");
            if (existing.RunId == rollover.NextRunId && !IsPristineNewOperation(existing))
                throw new InvalidDataException("The prepared rollover successor is not pristine.");
            return new(rollover.NextRunId, false, rollover.PreviousRunId, NeedsMachineRollover: true);
        }

        if (rollover is { State: UserJournalRolloverState.Committed } && rollover.PreviousRunId == existing.RunId)
            throw new InvalidDataException("The active user journal contradicts the committed rollover record.");
        if (rollover is { State: UserJournalRolloverState.Committed } &&
            rollover.NextRunId == existing.RunId && IsPristineNewOperation(existing))
            return new(existing.RunId, false);

        if (action != BootstrapperAction.ExecuteUpgrade)
            return new(existing.RunId, true);

        if (existing.State == UpgradeJournalState.RolledBack &&
            !string.IsNullOrWhiteSpace(existing.FailureCode) && HasNoIncompleteUserState(existing))
            return new(candidateOperationId, false, existing.RunId, NeedsMachineRollover: true);

        return new(existing.RunId, true);
    }

    private static async ValueTask RolloverMachineJournalAsync(Guid previousOperationId, Guid nextOperationId,
        CancellationToken cancellationToken)
    {
        using var client = await new OriginalUserCallerTransport().OpenAsync(nextOperationId,
            MachineCommandClient.MaximumTimeout, cancellationToken).ConfigureAwait(false);
        var request = new MachineIpcRequest(MachineIpcFrameCodec.CurrentVersion, Guid.NewGuid(),
            MachineIpcOperation.Rollover, previousOperationId, nextOperationId);
        var result = await client.SendAsync(request, MachineCommandClient.MaximumTimeout, cancellationToken).ConfigureAwait(false);
        if (result.Status != MachineDispatcherStatus.Completed || result.Code != MachineDispatcherCode.None)
            throw new InvalidOperationException("The protected machine journal rollover was not confirmed.");
    }

    private static bool HasNoIncompleteUserState(UpgradeJournalDocument journal)
    {
        foreach (var intent in journal.Events.Where(item => item.Kind == UpgradeJournalEventKind.MutationIntent && item.Mutation is not null))
        {
            if (!journal.Events.Any(item => item.Sequence > intent.Sequence && item.Mutation == intent.Mutation &&
                item.Kind is UpgradeJournalEventKind.MutationPrepared or UpgradeJournalEventKind.MutationApplied or
                    UpgradeJournalEventKind.MutationCompensated or UpgradeJournalEventKind.MutationFailed))
                return false;
        }
        return true;
    }

    private static bool IsPristineNewOperation(UpgradeJournalDocument journal) =>
        journal.SchemaVersion == UpgradeJournalDocument.CurrentSchemaVersion && journal.Revision == 0 &&
        journal.State == UpgradeJournalState.InProgress && journal.Phase == UpgradePhase.Preflight &&
        journal.Events.Count == 0 && journal.Recovery is null;

    private static BootstrapperPreparation Blocked(string code) =>
        new(null, [new BootstrapperBlocker(code)]);
}
