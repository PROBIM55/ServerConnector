using System.Security.Cryptography;
using System.Text.Json;

namespace Connector.Upgrade.UserState;

public sealed partial class UserStateSnapshotService
{
    private const string ApplyJournalFileName = "apply-journal.json";

    public async ValueTask<IReadOnlyList<UserStatePendingRecovery>> ListPendingRecoveryOperationsAsync(
        string expectedUserSid,
        IProtectedUserStateStage stage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var currentSid = AssertSameUser(expectedUserSid);
        var stageRoot = await ValidateStageAsync(stage, currentSid, cancellationToken).ConfigureAwait(false);
        var operationsRoot = Path.Combine(stageRoot, "operations");
        if (!Directory.Exists(operationsRoot)) return [];
        EnsureNoReparsePoints(operationsRoot);

        var result = new List<UserStatePendingRecovery>();
        foreach (var directory in Directory.EnumerateDirectories(operationsRoot).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(directory);
            var operationId = Path.GetFileName(directory);
            if (!SafeId.IsMatch(operationId))
            {
                throw new UserStateIntegrityException("Protected operations stage contains an unsafe operation identifier.");
            }

            var journalPath = Path.Combine(directory, ApplyJournalFileName);
            if (File.Exists(journalPath))
            {
                result.Add(new UserStatePendingRecovery(operationId, UserStatePendingRecoveryStatus.Recoverable));
                continue;
            }

            var temporaryJournals = FindJournalTemporaryFiles(directory);
            var status = UserStatePendingRecoveryStatus.NeedsManualRecovery;
            if (temporaryJournals.Count == 1)
            {
                try
                {
                    var temporary = await ReadJournalFileAsync(temporaryJournals[0], cancellationToken).ConfigureAwait(false);
                    if (FixedEquals(temporary.OperationId, operationId) && FixedEquals(temporary.OwnerSid, currentSid))
                    {
                        status = UserStatePendingRecoveryStatus.RecoverableTemporaryJournal;
                    }
                }
                catch (UserStateSnapshotException)
                {
                    status = UserStatePendingRecoveryStatus.NeedsManualRecovery;
                }
                catch (JsonException)
                {
                    status = UserStatePendingRecoveryStatus.NeedsManualRecovery;
                }
            }

            result.Add(new UserStatePendingRecovery(operationId, status));
        }

        return result;
    }

    public async ValueTask<UserStateRecoveryResult> RecoverInterruptedApplyAsync(
        string expectedUserSid,
        string operationId,
        IProtectedUserStateStage stage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var currentSid = AssertSameUser(expectedUserSid);
        if (!SafeId.IsMatch(operationId)) throw new ArgumentException("Recovery operation identifier is invalid.", nameof(operationId));
        var stageRoot = await ValidateStageAsync(stage, currentSid, cancellationToken).ConfigureAwait(false);
        var journal = await RecoverInterruptedApplyCoreAsync(stageRoot, operationId, currentSid, cancellationToken).ConfigureAwait(false);
        return new UserStateRecoveryResult(operationId, journal.Snapshot.FileCount, journal.Snapshot.PayloadBytes);
    }

    private async ValueTask CompleteApplyFromJournalAsync(
        string stageRoot,
        string operationRoot,
        CommittedSnapshot committed,
        ApplyJournal journal,
        IReadOnlyList<NormalizedRoot> targets,
        CancellationToken cancellationToken)
    {
        var plan = await ValidateAndPlanRecoveryAsync(
            stageRoot,
            operationRoot,
            committed,
            journal,
            targets,
            cancellationToken).ConfigureAwait(false);
        await ExecuteRecoveryPlanAsync(operationRoot, committed, journal, plan, targets, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ApplyJournal> RecoverInterruptedApplyCoreAsync(
        string stageRoot,
        string operationId,
        string currentSid,
        CancellationToken cancellationToken)
    {
        var operationRoot = Path.Combine(stageRoot, "operations", operationId);
        if (!Directory.Exists(operationRoot)) throw new UserStateIntegrityException("Recovery operation workspace is missing.");
        EnsureNoReparsePoints(operationRoot);
        var journal = await ReadApplyJournalAsync(operationRoot, cancellationToken).ConfigureAwait(false);
        if (journal.SchemaVersion != SchemaVersion ||
            !FixedEquals(journal.OperationId, operationId) ||
            !FixedEquals(journal.OwnerSid, currentSid))
        {
            throw new UserStateIntegrityException("Recovery journal identity is invalid.");
        }

        var committed = await LoadCommittedSnapshotAsync(stageRoot, journal.Snapshot, cancellationToken).ConfigureAwait(false);
        var targets = GetCanonicalRoots();
        ValidateTargetMapping(committed.Manifest, targets);
        AssertNoOverlap(targets.Select(item => item.Path), [stageRoot], "Restore targets overlap the protected stage.");
        AssertSameVolume(stageRoot, targets);
        var plan = await ValidateAndPlanRecoveryAsync(
            stageRoot,
            operationRoot,
            committed,
            journal,
            targets,
            cancellationToken).ConfigureAwait(false);
        await ExecuteRecoveryPlanAsync(operationRoot, committed, journal, plan, targets, cancellationToken).ConfigureAwait(false);
        return journal;
    }

    private async ValueTask<IReadOnlyList<RecoveryPlanRoot>> ValidateAndPlanRecoveryAsync(
        string stageRoot,
        string operationRoot,
        CommittedSnapshot committed,
        ApplyJournal journal,
        IReadOnlyList<NormalizedRoot> targets,
        CancellationToken cancellationToken)
    {
        if (!FixedEquals(journal.Snapshot.OwnerSid, journal.OwnerSid) ||
            !FixedEquals(journal.ExpectedTargets.OwnerSid, journal.OwnerSid) ||
            !FixedEquals(journal.ExpectedSnapshot.OwnerSid, journal.OwnerSid))
        {
            throw new UserStateIntegrityException("Recovery journal SID fence is invalid.");
        }

        var expectedSnapshot = CreateSnapshotFingerprint(journal.OwnerSid, committed.Manifest);
        AssertFingerprintMatches(journal.ExpectedSnapshot, expectedSnapshot);
        if (journal.Roots.Count != targets.Count)
        {
            throw new UserStateIntegrityException("Recovery journal root count is invalid.");
        }

        var plan = new List<RecoveryPlanRoot>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = journal.Roots.SingleOrDefault(item => FixedEquals(item.RootId, target.Id))
                ?? throw new UserStateIntegrityException("Recovery journal is missing a canonical root.");
            var expectedIncoming = Path.Combine(operationRoot, "incoming", target.Id);
            var expectedBackup = Path.Combine(operationRoot, "backup", target.Id);
            if (!FixedEquals(root.TargetPath, target.Path) ||
                !FixedEquals(root.IncomingPath, expectedIncoming) ||
                !FixedEquals(root.BackupPath, expectedBackup) ||
                !IsSameOrDescendant(root.IncomingPath, operationRoot) ||
                !IsSameOrDescendant(root.BackupPath, operationRoot))
            {
                throw new UserStateIntegrityException("Recovery journal paths do not match the canonical policy.");
            }

            EnsureNoReparsePoints(Path.GetDirectoryName(root.IncomingPath)!);
            EnsureNoReparsePoints(Path.GetDirectoryName(root.BackupPath)!);
            var expectedBefore = journal.ExpectedTargets.Roots.Single(item => FixedEquals(item.RootId, target.Id));
            var expectedAfter = journal.ExpectedSnapshot.Roots.Single(item => FixedEquals(item.RootId, target.Id));
            var targetState = await InspectSingleRootAsync(journal.OwnerSid, target.Id, target.Path, committed.Manifest.Limits, cancellationToken).ConfigureAwait(false);
            var incomingState = await InspectSingleRootAsync(journal.OwnerSid, target.Id, root.IncomingPath, committed.Manifest.Limits, cancellationToken).ConfigureAwait(false);
            var backupState = await InspectSingleRootAsync(journal.OwnerSid, target.Id, root.BackupPath, committed.Manifest.Limits, cancellationToken).ConfigureAwait(false);
            var absent = CreateAbsentRootFingerprint(target.Id);

            RecoveryDisposition disposition;
            if (RootFingerprintEquals(targetState, expectedAfter) &&
                RootFingerprintEquals(incomingState, absent) &&
                (RootFingerprintEquals(backupState, expectedBefore) || RootFingerprintEquals(backupState, absent)))
            {
                disposition = RecoveryDisposition.AlreadyApplied;
            }
            else if (RootFingerprintEquals(targetState, expectedBefore) &&
                     RootFingerprintEquals(backupState, absent) &&
                     (RootFingerprintEquals(incomingState, expectedAfter) ||
                      (!expectedAfter.Exists && RootFingerprintEquals(incomingState, absent))))
            {
                disposition = RecoveryDisposition.Prepared;
            }
            else if (RootFingerprintEquals(targetState, absent) &&
                     RootFingerprintEquals(backupState, expectedBefore) &&
                     (RootFingerprintEquals(incomingState, expectedAfter) ||
                      (!expectedAfter.Exists && RootFingerprintEquals(incomingState, absent))))
            {
                disposition = RecoveryDisposition.TargetMovedToBackup;
            }
            else
            {
                throw new UserStateChangedException($"Recovery refused unexpected state for canonical root {target.Id}.");
            }

            plan.Add(new RecoveryPlanRoot(root, expectedBefore, expectedAfter, disposition));
        }

        return plan;
    }

    private async ValueTask ExecuteRecoveryPlanAsync(
        string operationRoot,
        CommittedSnapshot committed,
        ApplyJournal journal,
        IReadOnlyList<RecoveryPlanRoot> plan,
        IReadOnlyList<NormalizedRoot> targets,
        CancellationToken cancellationToken)
    {
        var currentJournal = journal;
        foreach (var planned in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = planned.JournalRoot;
            var context = new UserStateApplyFaultContext(
                journal.OperationId,
                root.RootId,
                root.TargetPath,
                root.IncomingPath,
                root.BackupPath);
            _faultInjector.OnFaultPoint(UserStateApplyFaultPoint.BeforeRootMutation, context);
            AssertProtectedOperationPath(
                root.IncomingPath,
                operationRoot,
                allowMissing: !planned.ExpectedAfter.Exists || planned.Disposition == RecoveryDisposition.AlreadyApplied);
            AssertProtectedOperationPath(root.BackupPath, operationRoot, allowMissing: true);

            if (planned.Disposition == RecoveryDisposition.Prepared && planned.ExpectedBefore.Exists)
            {
                EnsureDirectoryOwnedPath(Path.GetDirectoryName(root.BackupPath)!, operationRoot);
                EnsureNoReparsePoints(root.TargetPath);
                Directory.Move(root.TargetPath, root.BackupPath);
                currentJournal = UpdateJournalRootState(currentJournal, root.RootId, ApplyJournalRootState.TargetMovedToBackup);
                await WriteApplyJournalAsync(operationRoot, currentJournal, cancellationToken).ConfigureAwait(false);
                _faultInjector.OnFaultPoint(UserStateApplyFaultPoint.AfterTargetMovedToBackup, context);
            }

            if (planned.Disposition != RecoveryDisposition.AlreadyApplied && planned.ExpectedAfter.Exists)
            {
                EnsureNoReparsePoints(root.IncomingPath);
                if (Directory.Exists(root.TargetPath))
                {
                    throw new UserStateChangedException($"Canonical target reappeared during recovery: {root.RootId}.");
                }

                EnsureCanonicalTargetParent(root.TargetPath);
                Directory.Move(root.IncomingPath, root.TargetPath);
                currentJournal = UpdateJournalRootState(currentJournal, root.RootId, ApplyJournalRootState.SnapshotMovedToTarget);
                await WriteApplyJournalAsync(operationRoot, currentJournal, cancellationToken).ConfigureAwait(false);
                _faultInjector.OnFaultPoint(UserStateApplyFaultPoint.AfterSnapshotMovedToTarget, context);
            }
            else if (!planned.ExpectedAfter.Exists)
            {
                currentJournal = UpdateJournalRootState(currentJournal, root.RootId, ApplyJournalRootState.SnapshotMovedToTarget);
                await WriteApplyJournalAsync(operationRoot, currentJournal, cancellationToken).ConfigureAwait(false);
            }
        }

        var finalState = await InspectTargetsCoreAsync(journal.OwnerSid, targets, committed.Manifest.Limits, cancellationToken).ConfigureAwait(false);
        AssertFingerprintMatches(journal.ExpectedSnapshot, finalState);
        foreach (var root in currentJournal.Roots)
        {
            if (Directory.Exists(root.BackupPath)) DeleteOwnedTree(root.BackupPath, operationRoot);
        }

        DeleteOwnedTree(operationRoot, Path.GetDirectoryName(operationRoot)!);
    }

    private async ValueTask WriteApplyJournalAsync(
        string operationRoot,
        ApplyJournal journal,
        CancellationToken cancellationToken)
    {
        EnsureNoReparsePoints(operationRoot);
        var journalPath = Path.Combine(operationRoot, ApplyJournalFileName);
        var temporary = journalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await WriteJsonAsync(temporary, journal, cancellationToken).ConfigureAwait(false);
        var firstRoot = journal.Roots.First();
        _faultInjector.OnFaultPoint(
            UserStateApplyFaultPoint.AfterJournalTemporaryFlushedBeforePublication,
            new UserStateApplyFaultContext(
                journal.OperationId,
                firstRoot.RootId,
                firstRoot.TargetPath,
                firstRoot.IncomingPath,
                firstRoot.BackupPath));
        DurableFilePublication.Publish(temporary, journalPath);
    }

    private static async ValueTask<ApplyJournal> ReadApplyJournalAsync(
        string operationRoot,
        CancellationToken cancellationToken)
    {
        var journalPath = Path.Combine(operationRoot, ApplyJournalFileName);
        if (File.Exists(journalPath))
        {
            return await ReadJournalFileAsync(journalPath, cancellationToken).ConfigureAwait(false);
        }

        var temporaryJournals = FindJournalTemporaryFiles(operationRoot);
        if (temporaryJournals.Count != 1)
        {
            throw new UserStateIntegrityException("Recovery journal is missing or ambiguous; manual recovery is required.");
        }

        var recovered = await ReadJournalFileAsync(temporaryJournals[0], cancellationToken).ConfigureAwait(false);
        DurableFilePublication.Publish(temporaryJournals[0], journalPath);
        return recovered;
    }

    private static IReadOnlyList<string> FindJournalTemporaryFiles(string operationRoot)
    {
        var result = new List<string>();
        foreach (var path in Directory.EnumerateFiles(operationRoot, ApplyJournalFileName + ".*.tmp", SearchOption.TopDirectoryOnly))
        {
            EnsureNoReparsePoints(path);
            var name = Path.GetFileName(path);
            var prefix = ApplyJournalFileName + ".";
            var id = name[prefix.Length..^4];
            if (!Guid.TryParseExact(id, "N", out _))
            {
                throw new UserStateIntegrityException("Protected operations stage contains an unsafe journal temporary name.");
            }

            result.Add(path);
        }

        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static async ValueTask<ApplyJournal> ReadJournalFileAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        EnsureNoReparsePoints(journalPath);
        var info = new FileInfo(journalPath);
        if (info.Length is <= 0 or > 1024 * 1024) throw new UserStateIntegrityException("Recovery journal size is invalid.");
        await using var stream = OpenStableRead(journalPath);
        return await JsonSerializer.DeserializeAsync<ApplyJournal>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new UserStateIntegrityException("Recovery journal is invalid.");
    }

    private async ValueTask<UserStateRootFingerprint> InspectSingleRootAsync(
        string ownerSid,
        string rootId,
        string path,
        UserStateSnapshotLimits limits,
        CancellationToken cancellationToken)
    {
        var fingerprint = await InspectTargetsCoreAsync(
            ownerSid,
            [new NormalizedRoot(rootId, NormalizeAbsolute(path))],
            limits,
            cancellationToken).ConfigureAwait(false);
        return fingerprint.Roots.Single();
    }

    private static UserStateTargetFingerprint CreateSnapshotFingerprint(string ownerSid, SnapshotManifest manifest)
    {
        var roots = new List<UserStateRootFingerprint>(manifest.Roots.Count);
        foreach (var root in manifest.Roots)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendHash(hash, root.Id);
            AppendHash(hash, root.Exists);
            var entries = manifest.Entries.Where(item => FixedEquals(item.RootId, root.Id)).ToArray();
            var bytes = 0L;
            foreach (var entry in entries)
            {
                AppendHash(hash, entry.RelativePath);
                AppendHash(hash, (int)entry.Kind);
                AppendHash(hash, entry.Length);
                AppendHash(hash, entry.LastWriteUtcTicks);
                AppendHash(hash, entry.Attributes);
                if (entry.Kind == SnapshotEntryKind.File)
                {
                    AppendHash(hash, entry.Sha256!);
                    bytes = checked(bytes + entry.Length);
                }
            }

            roots.Add(new UserStateRootFingerprint(
                root.Id,
                root.Exists,
                entries.Length,
                bytes,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
        }

        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var root in roots)
        {
            AppendHash(aggregate, root.RootId);
            AppendHash(aggregate, root.Exists);
            AppendHash(aggregate, root.EntryCount);
            AppendHash(aggregate, root.PayloadBytes);
            AppendHash(aggregate, root.FingerprintSha256);
        }

        return new UserStateTargetFingerprint(
            ownerSid,
            Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant(),
            roots);
    }

    private static UserStateRootFingerprint CreateAbsentRootFingerprint(string rootId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHash(hash, rootId);
        AppendHash(hash, false);
        return new UserStateRootFingerprint(
            rootId,
            false,
            0,
            0,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static bool RootFingerprintEquals(UserStateRootFingerprint left, UserStateRootFingerprint right) =>
        FixedEquals(left.RootId, right.RootId) &&
        left.Exists == right.Exists &&
        left.EntryCount == right.EntryCount &&
        left.PayloadBytes == right.PayloadBytes &&
        FixedEquals(left.FingerprintSha256, right.FingerprintSha256);

    private static ApplyJournal UpdateJournalRootState(
        ApplyJournal journal,
        string rootId,
        ApplyJournalRootState state) => journal with
        {
            Roots = journal.Roots
                .Select(item => FixedEquals(item.RootId, rootId) ? item with { State = state } : item)
                .ToArray()
        };

    private static void AssertProtectedOperationPath(string path, string operationRoot, bool allowMissing)
    {
        var normalized = NormalizeAbsolute(path);
        if (!IsSameOrDescendant(normalized, NormalizeAbsolute(operationRoot)))
        {
            throw new UserStateIntegrityException("Operation path escaped protected staging.");
        }

        if (!Directory.Exists(normalized))
        {
            if (allowMissing) return;
            throw new UserStateIntegrityException("Protected operation payload is missing.");
        }

        EnsureNoReparsePoints(normalized);
    }

    private static void EnsureCanonicalTargetParent(string targetPath)
    {
        var parent = Path.GetDirectoryName(NormalizeAbsolute(targetPath))
            ?? throw new UserStateSnapshotException("Canonical target does not have a parent directory.");
        EnsureNoReparsePoints(parent);
        Directory.CreateDirectory(parent);
        EnsureNoReparsePoints(parent);
    }

    private enum ApplyJournalRootState
    {
        Prepared = 0,
        TargetMovedToBackup = 1,
        SnapshotMovedToTarget = 2
    }

    private enum RecoveryDisposition
    {
        Prepared,
        TargetMovedToBackup,
        AlreadyApplied
    }

    private sealed record ApplyJournal(
        int SchemaVersion,
        string OperationId,
        string OwnerSid,
        UserStateSnapshotHandle Snapshot,
        UserStateApplyPurpose Purpose,
        UserStateTargetFingerprint ExpectedTargets,
        UserStateTargetFingerprint ExpectedSnapshot,
        IReadOnlyList<ApplyJournalRoot> Roots);

    private sealed record ApplyJournalRoot(
        string RootId,
        string TargetPath,
        string IncomingPath,
        string BackupPath,
        ApplyJournalRootState State);

    private sealed record RecoveryPlanRoot(
        ApplyJournalRoot JournalRoot,
        UserStateRootFingerprint ExpectedBefore,
        UserStateRootFingerprint ExpectedAfter,
        RecoveryDisposition Disposition);
}
