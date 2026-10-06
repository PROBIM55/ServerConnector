using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.UserState;

public sealed partial class UserStateSnapshotService
{
    private const int SchemaVersion = 1;
    private const string ManifestFileName = "manifest.json";
    private const string CommitFileName = "commit.json";
    private const long AbsoluteManifestLimit = 512L * 1024 * 1024;
    private static readonly Regex SafeId = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IUserIdentityProvider _identityProvider;
    private readonly IUserStatePathPolicy _pathPolicy;
    private readonly IUserStateApplyFaultInjector _faultInjector;

    public UserStateSnapshotService()
        : this(
            new WindowsUserIdentityProvider(),
            new CurrentUserLegacyStatePathPolicy(),
            NoUserStateApplyFaultInjector.Instance)
    {
    }

    internal UserStateSnapshotService(
        IUserIdentityProvider identityProvider,
        IUserStatePathPolicy pathPolicy,
        IUserStateApplyFaultInjector? faultInjector = null)
    {
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        _faultInjector = faultInjector ?? NoUserStateApplyFaultInjector.Instance;
    }

    public async ValueTask<UserStateSnapshotHandle> CreateSnapshotAsync(
        UserStateSnapshotRequest request,
        IProtectedUserStateStage stage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stage);
        var currentSid = AssertSameUser(request.ExpectedUserSid);
        var limits = request.Limits ?? UserStateSnapshotLimits.Default;
        limits.Validate();

        var roots = GetCanonicalRoots();
        var stageRoot = await ValidateStageAsync(stage, currentSid, cancellationToken).ConfigureAwait(false);
        AssertNoOverlap(roots.Select(item => item.Path), [stageRoot], "Snapshot sources overlap the protected stage.");

        var inventories = new List<RootInventory>(roots.Count);
        var entryCount = 0;
        var totalBytes = 0L;
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inventory = InventoryRoot(root, limits, ref entryCount, ref totalBytes);
            inventories.Add(inventory);
        }

        var snapshotId = Guid.NewGuid().ToString("N");
        var snapshotsRoot = Path.Combine(stageRoot, "snapshots");
        EnsureDirectoryOwnedPath(snapshotsRoot, stageRoot);
        var partialRoot = Path.Combine(snapshotsRoot, snapshotId + ".partial");
        var committedRoot = Path.Combine(snapshotsRoot, snapshotId);
        if (Directory.Exists(partialRoot) || Directory.Exists(committedRoot))
        {
            throw new UserStateSnapshotException("The generated snapshot identifier already exists in the protected stage.");
        }

        Directory.CreateDirectory(partialRoot);
        try
        {
            EnsureNoReparsePoints(partialRoot);
            var manifestEntries = new List<SnapshotEntry>(entryCount);
            var copiedFiles = 0;
            var copiedBytes = 0L;

            foreach (var inventory in inventories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payloadRoot = Path.Combine(partialRoot, "payload", inventory.Root.Id);
                if (inventory.Exists)
                {
                    EnsureDirectoryOwnedPath(payloadRoot, partialRoot);
                }

                foreach (var entry in inventory.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? sha256 = null;
                    if (entry.Kind == SnapshotEntryKind.Directory)
                    {
                        EnsureDirectoryOwnedPath(ResolveRelative(payloadRoot, entry.RelativePath), partialRoot);
                    }
                    else
                    {
                        var sourcePath = ResolveRelative(inventory.Root.Path, entry.RelativePath);
                        var destinationPath = ResolveRelative(payloadRoot, entry.RelativePath);
                        EnsureDirectoryOwnedPath(Path.GetDirectoryName(destinationPath)!, partialRoot);
                        sha256 = await CopyStableFileAsync(
                            sourcePath,
                            destinationPath,
                            entry,
                            limits.BufferBytes,
                            cancellationToken).ConfigureAwait(false);
                        copiedFiles++;
                        copiedBytes = checked(copiedBytes + entry.Length);
                    }

                    manifestEntries.Add(new SnapshotEntry(
                        inventory.Root.Id,
                        entry.RelativePath,
                        entry.Kind,
                        entry.Length,
                        sha256,
                        entry.LastWriteUtcTicks,
                        entry.Attributes));
                }
            }

            await VerifySourcesUnchangedAsync(inventories, limits, manifestEntries, cancellationToken).ConfigureAwait(false);

            var manifest = new SnapshotManifest(
                SchemaVersion,
                snapshotId,
                currentSid,
                DateTimeOffset.UtcNow,
                limits,
                inventories.Select(item => new SnapshotRoot(item.Root.Id, item.Exists)).ToArray(),
                manifestEntries);
            var manifestPath = Path.Combine(partialRoot, ManifestFileName);
            await WriteJsonAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            var manifestInfo = new FileInfo(manifestPath);
            if (manifestInfo.Length > limits.MaxManifestBytes)
            {
                throw new UserStateSnapshotException($"Snapshot manifest exceeds the configured {limits.MaxManifestBytes} byte limit.");
            }

            var manifestSha256 = await HashFileAsync(manifestPath, limits.BufferBytes, cancellationToken).ConfigureAwait(false);
            var handle = new UserStateSnapshotHandle(
                snapshotId,
                currentSid,
                manifestInfo.Length,
                manifestSha256,
                copiedFiles,
                copiedBytes);
            await WriteJsonAsync(Path.Combine(partialRoot, CommitFileName), new SnapshotCommit(handle), cancellationToken)
                .ConfigureAwait(false);

            MakeFilesReadOnly(partialRoot);
            Directory.Move(partialRoot, committedRoot);
            return handle;
        }
        catch
        {
            if (Directory.Exists(partialRoot)) DeleteOwnedTree(partialRoot, snapshotsRoot);
            throw;
        }
    }

    public ValueTask<UserStateTargetFingerprint> InspectTargetsAsync(
        string expectedUserSid,
        UserStateSnapshotLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        var currentSid = AssertSameUser(expectedUserSid);
        return InspectTargetsCoreAsync(currentSid, GetCanonicalRoots(), limits ?? UserStateSnapshotLimits.Default, cancellationToken);
    }

    public async ValueTask<UserStateApplyResult> ApplySnapshotAsync(
        UserStateApplyRequest request,
        IProtectedUserStateStage stage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stage);
        var currentSid = AssertSameUser(request.ExpectedUserSid);
        if (!FixedEquals(currentSid, request.Snapshot.OwnerSid) ||
            !FixedEquals(currentSid, request.ExpectedTargets.OwnerSid))
        {
            throw new UserStateSnapshotException("Snapshot, target fingerprint and current Windows SID must match.");
        }

        var stageRoot = await ValidateStageAsync(stage, currentSid, cancellationToken).ConfigureAwait(false);
        var committed = await LoadCommittedSnapshotAsync(stageRoot, request.Snapshot, cancellationToken).ConfigureAwait(false);
        var targets = GetCanonicalRoots();
        ValidateTargetMapping(committed.Manifest, targets);
        AssertNoOverlap(targets.Select(item => item.Path), [stageRoot], "Restore targets overlap the protected stage.");
        AssertSameVolume(stageRoot, targets);

        var limits = committed.Manifest.Limits;
        limits.Validate();
        var before = await InspectTargetsCoreAsync(currentSid, targets, limits, cancellationToken).ConfigureAwait(false);
        AssertFingerprintMatches(request.ExpectedTargets, before);
        if (request.Purpose == UserStateApplyPurpose.HydrateFreshTarget &&
            before.Roots.Any(item => item.Exists && item.EntryCount != 0))
        {
            throw new UserStateSnapshotException("Fresh-target hydration refuses a non-empty target root.");
        }

        var operationId = Guid.NewGuid().ToString("N");
        var operationRoot = Path.Combine(stageRoot, "operations", operationId);
        var journalWritten = false;
        try
        {
            EnsureDirectoryOwnedPath(operationRoot, stageRoot);
            var prepared = new List<ApplyJournalRoot>(targets.Count);
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshotRoot = committed.Manifest.Roots.Single(item => FixedEquals(item.Id, target.Id));
                var incoming = Path.Combine(operationRoot, "incoming", target.Id);
                var backup = Path.Combine(operationRoot, "backup", target.Id);

                if (snapshotRoot.Exists)
                {
                    EnsureDirectoryOwnedPath(incoming, operationRoot);
                    await MaterializeRootAsync(committed, target.Id, incoming, limits, cancellationToken).ConfigureAwait(false);
                }

                prepared.Add(new ApplyJournalRoot(
                    target.Id,
                    target.Path,
                    incoming,
                    backup,
                    ApplyJournalRootState.Prepared));
            }

            var immediatelyBeforeSwap = await InspectTargetsCoreAsync(currentSid, targets, limits, cancellationToken).ConfigureAwait(false);
            AssertFingerprintMatches(request.ExpectedTargets, immediatelyBeforeSwap);
            var expectedSnapshot = CreateSnapshotFingerprint(currentSid, committed.Manifest);
            var journal = new ApplyJournal(
                SchemaVersion,
                operationId,
                currentSid,
                request.Snapshot,
                request.Purpose,
                request.ExpectedTargets,
                expectedSnapshot,
                prepared);
            await WriteApplyJournalAsync(operationRoot, journal, cancellationToken).ConfigureAwait(false);
            journalWritten = true;
            await CompleteApplyFromJournalAsync(stageRoot, operationRoot, committed, journal, targets, cancellationToken).ConfigureAwait(false);

            return new UserStateApplyResult(operationId, request.Purpose, request.Snapshot.FileCount, request.Snapshot.PayloadBytes);
        }
        catch (UserStateInjectedProcessCrashException)
        {
            throw;
        }
        catch (Exception applyError) when (journalWritten)
        {
            try
            {
                await RecoverInterruptedApplyCoreAsync(stageRoot, operationId, currentSid, cancellationToken).ConfigureAwait(false);
                return new UserStateApplyResult(operationId, request.Purpose, request.Snapshot.FileCount, request.Snapshot.PayloadBytes);
            }
            catch (Exception recoveryError)
            {
                throw new UserStateSnapshotException(
                    $"User-state restore failed and durable recovery for operation {operationId} also failed. The protected operation workspace must be retained.",
                    new AggregateException(applyError, recoveryError));
            }
        }
        catch
        {
            if (Directory.Exists(operationRoot)) DeleteOwnedTree(operationRoot, stageRoot);
            throw;
        }
    }

    private async ValueTask<CommittedSnapshot> LoadCommittedSnapshotAsync(
        string stageRoot,
        UserStateSnapshotHandle expected,
        CancellationToken cancellationToken)
    {
        ValidateHandle(expected);
        var snapshotRoot = Path.Combine(stageRoot, "snapshots", expected.SnapshotId);
        if (!Directory.Exists(snapshotRoot))
        {
            throw new UserStateIntegrityException("Committed snapshot directory is missing.");
        }

        EnsureNoReparsePoints(snapshotRoot);
        var commitPath = Path.Combine(snapshotRoot, CommitFileName);
        var manifestPath = Path.Combine(snapshotRoot, ManifestFileName);
        if (!File.Exists(commitPath) || !File.Exists(manifestPath))
        {
            throw new UserStateIntegrityException("Snapshot is incomplete: commit or manifest is missing.");
        }

        EnsureNoReparsePoints(commitPath);
        EnsureNoReparsePoints(manifestPath);

        var commitInfo = new FileInfo(commitPath);
        if (commitInfo.Length is <= 0 or > 64 * 1024)
        {
            throw new UserStateIntegrityException("Snapshot commit marker has an invalid size.");
        }

        SnapshotCommit commit;
        await using (var commitStream = OpenStableRead(commitPath))
        {
            commit = await JsonSerializer.DeserializeAsync<SnapshotCommit>(commitStream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new UserStateIntegrityException("Snapshot commit marker is invalid.");
        }

        if (commit.Handle != expected)
        {
            throw new UserStateIntegrityException("Snapshot handle does not match the immutable commit marker.");
        }

        var manifestInfo = new FileInfo(manifestPath);
        if (manifestInfo.Length != expected.ManifestBytes || manifestInfo.Length is <= 0 or > AbsoluteManifestLimit)
        {
            throw new UserStateIntegrityException("Snapshot manifest size does not match its commit marker.");
        }

        var actualManifestHash = await HashFileAsync(manifestPath, UserStateSnapshotLimits.Default.BufferBytes, cancellationToken).ConfigureAwait(false);
        if (!FixedEquals(actualManifestHash, expected.ManifestSha256))
        {
            throw new UserStateIntegrityException("Snapshot manifest hash does not match its commit marker.");
        }

        SnapshotManifest manifest;
        await using (var manifestStream = OpenStableRead(manifestPath))
        {
            manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(manifestStream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new UserStateIntegrityException("Snapshot manifest is invalid.");
        }

        ValidateManifest(manifest, expected);
        return new CommittedSnapshot(snapshotRoot, manifest);
    }

    private async ValueTask MaterializeRootAsync(
        CommittedSnapshot committed,
        string rootId,
        string targetRoot,
        UserStateSnapshotLimits limits,
        CancellationToken cancellationToken)
    {
        var entries = committed.Manifest.Entries
            .Where(item => FixedEquals(item.RootId, rootId))
            .ToArray();
        foreach (var directory in entries.Where(item => item.Kind == SnapshotEntryKind.Directory).OrderBy(item => PathDepth(item.RelativePath)))
        {
            EnsureDirectoryOwnedPath(ResolveRelative(targetRoot, directory.RelativePath), targetRoot);
        }

        foreach (var file in entries.Where(item => item.Kind == SnapshotEntryKind.File))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = ResolveRelative(Path.Combine(committed.RootPath, "payload", rootId), file.RelativePath);
            var destinationPath = ResolveRelative(targetRoot, file.RelativePath);
            EnsureNoReparsePoints(sourcePath);
            EnsureDirectoryOwnedPath(Path.GetDirectoryName(destinationPath)!, targetRoot);
            var actualHash = await CopyAndHashAsync(sourcePath, destinationPath, file.Length, limits.BufferBytes, cancellationToken).ConfigureAwait(false);
            if (!FixedEquals(actualHash, file.Sha256))
            {
                throw new UserStateIntegrityException($"Snapshot payload hash mismatch for {rootId}/{file.RelativePath}.");
            }

            File.SetLastWriteTimeUtc(destinationPath, new DateTime(file.LastWriteUtcTicks, DateTimeKind.Utc));
            File.SetAttributes(destinationPath, (FileAttributes)file.Attributes);
        }

        foreach (var directory in entries.Where(item => item.Kind == SnapshotEntryKind.Directory).OrderByDescending(item => PathDepth(item.RelativePath)))
        {
            var path = ResolveRelative(targetRoot, directory.RelativePath);
            Directory.SetLastWriteTimeUtc(path, new DateTime(directory.LastWriteUtcTicks, DateTimeKind.Utc));
            File.SetAttributes(path, (FileAttributes)directory.Attributes);
        }
    }

    private async ValueTask<UserStateTargetFingerprint> InspectTargetsCoreAsync(
        string currentSid,
        IReadOnlyList<NormalizedRoot> targets,
        UserStateSnapshotLimits limits,
        CancellationToken cancellationToken)
    {
        limits.Validate();
        var count = 0;
        var bytes = 0L;
        var rootFingerprints = new List<UserStateRootFingerprint>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inventory = InventoryRoot(target, limits, ref count, ref bytes);
            using var rootHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendHash(rootHash, target.Id);
            AppendHash(rootHash, inventory.Exists);
            var rootPayloadBytes = 0L;
            foreach (var entry in inventory.Entries)
            {
                AppendHash(rootHash, entry.RelativePath);
                AppendHash(rootHash, (int)entry.Kind);
                AppendHash(rootHash, entry.Length);
                AppendHash(rootHash, entry.LastWriteUtcTicks);
                AppendHash(rootHash, entry.Attributes);
                if (entry.Kind == SnapshotEntryKind.File)
                {
                    var path = ResolveRelative(target.Path, entry.RelativePath);
                    var hash = await HashFileAsync(path, limits.BufferBytes, cancellationToken).ConfigureAwait(false);
                    AppendHash(rootHash, hash);
                    rootPayloadBytes = checked(rootPayloadBytes + entry.Length);
                }
            }

            rootFingerprints.Add(new UserStateRootFingerprint(
                target.Id,
                inventory.Exists,
                inventory.Entries.Count,
                rootPayloadBytes,
                Convert.ToHexString(rootHash.GetHashAndReset()).ToLowerInvariant()));
        }

        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var root in rootFingerprints)
        {
            AppendHash(aggregate, root.RootId);
            AppendHash(aggregate, root.Exists);
            AppendHash(aggregate, root.EntryCount);
            AppendHash(aggregate, root.PayloadBytes);
            AppendHash(aggregate, root.FingerprintSha256);
        }

        return new UserStateTargetFingerprint(
            currentSid,
            Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant(),
            rootFingerprints);
    }

    private static RootInventory InventoryRoot(
        NormalizedRoot root,
        UserStateSnapshotLimits limits,
        ref int totalEntries,
        ref long totalBytes)
    {
        EnsureNoReparsePoints(root.Path);
        if (!Directory.Exists(root.Path))
        {
            if (File.Exists(root.Path)) throw new UserStateSnapshotException($"User-state root is a file: {root.Id}.");
            return new RootInventory(root, false, []);
        }

        var entries = new List<InventoryEntry>();
        InventoryDirectory(root.Path, root.Path, 0, limits, entries, ref totalEntries, ref totalBytes);
        return new RootInventory(root, true, entries);
    }

    private static void InventoryDirectory(
        string rootPath,
        string directoryPath,
        int depth,
        UserStateSnapshotLimits limits,
        List<InventoryEntry> entries,
        ref int totalEntries,
        ref long totalBytes)
    {
        if (depth > limits.MaxDepth) throw new UserStateSnapshotException("User-state tree exceeds the configured depth limit.");
        var children = new DirectoryInfo(directoryPath)
            .EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0,
                IgnoreInaccessible = false
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var child in children)
        {
            child.Refresh();
            var attributes = child.Attributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0 || child.LinkTarget is not null)
            {
                throw new UserStateSnapshotException("Reparse points and symbolic links are forbidden in user-state trees.");
            }

            totalEntries = checked(totalEntries + 1);
            if (totalEntries > limits.MaxEntries) throw new UserStateSnapshotException("User-state tree exceeds the configured entry limit.");
            var relative = NormalizeRelative(Path.GetRelativePath(rootPath, child.FullName));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                var directory = (DirectoryInfo)child;
                entries.Add(new InventoryEntry(relative, SnapshotEntryKind.Directory, 0, directory.LastWriteTimeUtc.Ticks, (int)attributes));
                InventoryDirectory(rootPath, directory.FullName, depth + 1, limits, entries, ref totalEntries, ref totalBytes);
            }
            else
            {
                var file = (FileInfo)child;
                if (file.Length > limits.MaxFileBytes) throw new UserStateSnapshotException("A user-state file exceeds the configured file-size limit.");
                WindowsAlternateStreamGuard.ThrowIfNamedStreamsExist(file.FullName);
                totalBytes = checked(totalBytes + file.Length);
                if (totalBytes > limits.MaxTotalBytes) throw new UserStateSnapshotException("User-state payload exceeds the configured total-size limit.");
                entries.Add(new InventoryEntry(relative, SnapshotEntryKind.File, file.Length, file.LastWriteTimeUtc.Ticks, (int)attributes));
            }
        }
    }

    private static async ValueTask<string> CopyStableFileAsync(
        string sourcePath,
        string destinationPath,
        InventoryEntry expected,
        int bufferBytes,
        CancellationToken cancellationToken)
    {
        AssertFileMetadata(sourcePath, expected);
        var hash = await CopyAndHashAsync(sourcePath, destinationPath, expected.Length, bufferBytes, cancellationToken).ConfigureAwait(false);
        AssertFileMetadata(sourcePath, expected);
        return hash;
    }

    private static async ValueTask<string> CopyAndHashAsync(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        int bufferBytes,
        CancellationToken cancellationToken)
    {
        await using var source = OpenStableRead(sourcePath, bufferBytes);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferBytes);
        long copied = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, bufferBytes), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                copied = checked(copied + read);
                if (copied > expectedLength) throw new UserStateChangedException("A user-state file grew while it was copied.");
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        if (copied != expectedLength) throw new UserStateChangedException("A user-state file size changed while it was copied.");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async ValueTask VerifySourcesUnchangedAsync(
        IReadOnlyList<RootInventory> original,
        UserStateSnapshotLimits limits,
        IReadOnlyList<SnapshotEntry> manifestEntries,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var bytes = 0L;
        var manifestByPath = manifestEntries.ToDictionary(
            item => item.RootId + "\0" + item.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        foreach (var root in original)
        {
            var current = InventoryRoot(root.Root, limits, ref count, ref bytes);
            if (root.Exists != current.Exists || root.Entries.Count != current.Entries.Count ||
                !root.Entries.SequenceEqual(current.Entries))
            {
                throw new UserStateChangedException($"User-state root changed while snapshotting: {root.Root.Id}.");
            }

            foreach (var entry in current.Entries.Where(item => item.Kind == SnapshotEntryKind.File))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expected = manifestByPath[root.Root.Id + "\0" + entry.RelativePath];
                var actualHash = await HashFileAsync(
                    ResolveRelative(root.Root.Path, entry.RelativePath),
                    limits.BufferBytes,
                    cancellationToken).ConfigureAwait(false);
                if (!FixedEquals(expected.Sha256, actualHash))
                {
                    throw new UserStateChangedException($"User-state file changed while snapshotting: {root.Root.Id}/{entry.RelativePath}.");
                }
            }
        }
    }

    private static void ValidateManifest(SnapshotManifest manifest, UserStateSnapshotHandle expected)
    {
        if (manifest.SchemaVersion != SchemaVersion ||
            !FixedEquals(manifest.SnapshotId, expected.SnapshotId) ||
            !FixedEquals(manifest.OwnerSid, expected.OwnerSid))
        {
            throw new UserStateIntegrityException("Snapshot manifest identity is invalid.");
        }

        manifest.Limits.Validate();
        var roots = NormalizeManifestRootIds(manifest.Roots.Select(item => item.Id));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = 0;
        var bytes = 0L;
        foreach (var entry in manifest.Entries)
        {
            if (!roots.Contains(entry.RootId) || !seen.Add(entry.RootId + "\0" + entry.RelativePath))
            {
                throw new UserStateIntegrityException("Snapshot manifest contains an unknown or duplicate path.");
            }

            _ = NormalizeRelative(entry.RelativePath);
            if (entry.Kind == SnapshotEntryKind.File)
            {
                if (entry.Length < 0 || entry.Length > manifest.Limits.MaxFileBytes || !IsSha256(entry.Sha256))
                {
                    throw new UserStateIntegrityException("Snapshot file entry is invalid.");
                }

                files++;
                bytes = checked(bytes + entry.Length);
            }
            else if (entry.Kind != SnapshotEntryKind.Directory || entry.Length != 0 || entry.Sha256 is not null)
            {
                throw new UserStateIntegrityException("Snapshot directory entry is invalid.");
            }
        }

        if (manifest.Entries.Count > manifest.Limits.MaxEntries || bytes > manifest.Limits.MaxTotalBytes ||
            files != expected.FileCount || bytes != expected.PayloadBytes)
        {
            throw new UserStateIntegrityException("Snapshot totals do not match the immutable commit marker.");
        }
    }

    private static HashSet<string> NormalizeManifestRootIds(IEnumerable<string> rootIds)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in rootIds)
        {
            if (!SafeId.IsMatch(id) || !result.Add(id)) throw new UserStateIntegrityException("Snapshot root identifier is invalid or duplicated.");
        }

        return result;
    }

    private static IReadOnlyList<NormalizedRoot> NormalizeRoots(IReadOnlyList<UserStateRoot> roots, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(roots, parameterName);
        if (roots.Count == 0) throw new ArgumentException("At least one user-state root is required.", parameterName);
        var normalized = new List<NormalizedRoot>(roots.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (root is null || !SafeId.IsMatch(root.Id) || !ids.Add(root.Id))
            {
                throw new ArgumentException("User-state root identifiers must be unique safe identifiers.", parameterName);
            }

            if (string.IsNullOrWhiteSpace(root.Path) || !Path.IsPathFullyQualified(root.Path))
            {
                throw new ArgumentException("User-state roots must be absolute paths.", parameterName);
            }

            normalized.Add(new NormalizedRoot(root.Id, NormalizeAbsolute(root.Path)));
        }

        AssertNoOverlap(normalized.Select(item => item.Path), normalized.Select(item => item.Path), "User-state roots overlap each other.", allowIdentityPairs: true);
        return normalized;
    }

    private IReadOnlyList<NormalizedRoot> GetCanonicalRoots()
    {
        var roots = NormalizeRoots(_pathPolicy.GetCanonicalRoots(), "canonicalRoots");
        if (roots.Count != 3 ||
            roots.Count(item => FixedEquals(item.Id, LegacyUserStateRootIds.Structura)) != 1 ||
            roots.Count(item => FixedEquals(item.Id, LegacyUserStateRootIds.Platform)) != 1 ||
            roots.Count(item => FixedEquals(item.Id, LegacyUserStateRootIds.UnifiedPlatform)) != 1)
        {
            throw new UserStateSnapshotException("The runtime path policy must provide exactly the Structura, legacy Platform, and unified Platform canonical roots.");
        }

        return roots;
    }

    private static void AssertSameVolume(string stageRoot, IReadOnlyList<NormalizedRoot> targets)
    {
        var stageVolume = Path.GetPathRoot(NormalizeAbsolute(stageRoot));
        if (string.IsNullOrWhiteSpace(stageVolume) ||
            targets.Any(target => !FixedEquals(stageVolume, Path.GetPathRoot(target.Path))))
        {
            throw new UserStateSnapshotException("Protected stage and canonical targets must be on the same volume for atomic directory moves.");
        }
    }

    private async ValueTask<string> ValidateStageAsync(
        IProtectedUserStateStage stage,
        string expectedSid,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stage.RootPath) || !Path.IsPathFullyQualified(stage.RootPath))
        {
            throw new UserStateSnapshotException("Protected stage must expose an absolute root path.");
        }

        await stage.AssertProtectedForUserAsync(expectedSid, cancellationToken).ConfigureAwait(false);
        var stageRoot = NormalizeAbsolute(stage.RootPath);
        if (!Directory.Exists(stageRoot)) throw new UserStateSnapshotException("Protected stage root must already exist.");
        EnsureNoReparsePoints(stageRoot);
        return stageRoot;
    }

    private string AssertSameUser(string expectedSid)
    {
        if (string.IsNullOrWhiteSpace(expectedSid)) throw new ArgumentException("Expected Windows SID is required.", nameof(expectedSid));
        var currentSid = _identityProvider.GetCurrentUserSid();
        if (!FixedEquals(currentSid, expectedSid))
        {
            throw new UserStateSnapshotException("User-state operation refused because the current Windows SID changed.");
        }

        return currentSid;
    }

    private static void ValidateTargetMapping(SnapshotManifest manifest, IReadOnlyList<NormalizedRoot> targets)
    {
        if (manifest.Roots.Count != targets.Count ||
            manifest.Roots.Any(root => targets.All(target => !FixedEquals(target.Id, root.Id))))
        {
            throw new UserStateSnapshotException("Restore targets must map every snapshot root exactly once.");
        }
    }

    private static void ValidateHandle(UserStateSnapshotHandle handle)
    {
        if (!SafeId.IsMatch(handle.SnapshotId) || string.IsNullOrWhiteSpace(handle.OwnerSid) ||
            handle.ManifestBytes <= 0 || handle.ManifestBytes > AbsoluteManifestLimit ||
            !IsSha256(handle.ManifestSha256) || handle.FileCount < 0 || handle.PayloadBytes < 0)
        {
            throw new UserStateIntegrityException("Snapshot handle is invalid.");
        }
    }

    private static void AssertFingerprintMatches(UserStateTargetFingerprint expected, UserStateTargetFingerprint actual)
    {
        if (!FixedEquals(expected.OwnerSid, actual.OwnerSid) ||
            !FixedEquals(expected.FingerprintSha256, actual.FingerprintSha256) ||
            expected.Roots.Count != actual.Roots.Count)
        {
            throw new UserStateChangedException("Restore target changed after it was approved by the caller.");
        }
    }

    private static void AssertFileMetadata(string path, InventoryEntry expected)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || info.Length != expected.Length || info.LastWriteTimeUtc.Ticks != expected.LastWriteUtcTicks ||
            (int)info.Attributes != expected.Attributes || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
        {
            throw new UserStateChangedException("A user-state file changed before or during snapshot copy.");
        }
    }

    private static async ValueTask<string> HashFileAsync(string path, int bufferBytes, CancellationToken cancellationToken)
    {
        await using var stream = OpenStableRead(path, bufferBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferBytes);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, bufferBytes), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static FileStream OpenStableRead(string path, int bufferBytes = 64 * 1024) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferBytes,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async ValueTask WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void EnsureDirectoryOwnedPath(string path, string allowedRoot)
    {
        var normalized = NormalizeAbsolute(path);
        var allowed = NormalizeAbsolute(allowedRoot);
        if (!IsSameOrDescendant(normalized, allowed)) throw new UserStateSnapshotException("Directory creation escaped its allowed root.");
        EnsureNoReparsePoints(Path.GetDirectoryName(normalized) ?? normalized);
        Directory.CreateDirectory(normalized);
        EnsureNoReparsePoints(normalized);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = new DirectoryInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
        while (current is not null)
        {
            current.Refresh();
            if (current.Exists && ((current.Attributes & FileAttributes.ReparsePoint) != 0 || current.LinkTarget is not null))
            {
                throw new UserStateSnapshotException("Reparse points and symbolic links are forbidden in user-state paths.");
            }

            current = current.Parent;
        }

        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LinkTarget is not null)
            {
                throw new UserStateSnapshotException("Reparse points and symbolic links are forbidden in user-state paths.");
            }
        }
    }

    private static void AssertNoOverlap(
        IEnumerable<string> left,
        IEnumerable<string> right,
        string message,
        bool allowIdentityPairs = false)
    {
        var leftPaths = left.Select(NormalizeAbsolute).ToArray();
        var rightPaths = right.Select(NormalizeAbsolute).ToArray();
        for (var i = 0; i < leftPaths.Length; i++)
        {
            for (var j = 0; j < rightPaths.Length; j++)
            {
                if (allowIdentityPairs && i == j && leftPaths.Length == rightPaths.Length) continue;
                if (IsSameOrDescendant(leftPaths[i], rightPaths[j]) || IsSameOrDescendant(rightPaths[j], leftPaths[i]))
                {
                    throw new UserStateSnapshotException(message);
                }
            }
        }
    }

    private static string ResolveRelative(string root, string relative)
    {
        var normalizedRelative = NormalizeRelative(relative);
        var combined = NormalizeAbsolute(Path.Combine(root, normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrDescendant(combined, NormalizeAbsolute(root)))
        {
            throw new UserStateIntegrityException("Relative user-state path escaped its root.");
        }

        return combined;
    }

    private static string NormalizeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative))
        {
            throw new UserStateIntegrityException("User-state relative path is invalid.");
        }

        var normalized = relative.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Any(item => item.Length == 0 || item is "." or ".." || item.Contains(':') || item.IndexOf('\0') >= 0))
        {
            throw new UserStateIntegrityException("User-state relative path contains an unsafe segment.");
        }

        return string.Join('/', segments);
    }

    private static string NormalizeAbsolute(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        if (FixedEquals(candidate, root)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FixedEquals(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => Uri.IsHexDigit(character));

    private static int PathDepth(string relative) => relative.Count(character => character == '/') + 1;

    private static void MakeFilesReadOnly(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            File.SetAttributes(file, attributes | FileAttributes.ReadOnly);
        }
    }

    private static void DeleteOwnedTree(string path, string allowedParent)
    {
        var normalized = NormalizeAbsolute(path);
        var parent = NormalizeAbsolute(allowedParent);
        if (FixedEquals(normalized, parent) || !IsSameOrDescendant(normalized, parent))
        {
            throw new UserStateSnapshotException("Refused to delete a path outside the owned temporary scope.");
        }

        PrepareOwnedTreeForDeletion(normalized);
        Directory.Delete(normalized, recursive: true);
    }

    private static void PrepareOwnedTreeForDeletion(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            {
                Directory.Delete(directory);
                continue;
            }

            PrepareOwnedTreeForDeletion(directory);
            File.SetAttributes(directory, FileAttributes.Directory);
        }

        File.SetAttributes(root, FileAttributes.Directory);
    }

    private static void AppendHash(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendHash(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendHash(IncrementalHash hash, bool value) => hash.AppendData([value ? (byte)1 : (byte)0]);
    private static void AppendHash(IncrementalHash hash, int value) => hash.AppendData(BitConverter.GetBytes(value));
    private static void AppendHash(IncrementalHash hash, long value) => hash.AppendData(BitConverter.GetBytes(value));

    private enum SnapshotEntryKind
    {
        File = 1,
        Directory = 2
    }

    private sealed record NormalizedRoot(string Id, string Path);
    private sealed record InventoryEntry(string RelativePath, SnapshotEntryKind Kind, long Length, long LastWriteUtcTicks, int Attributes);
    private sealed record RootInventory(NormalizedRoot Root, bool Exists, IReadOnlyList<InventoryEntry> Entries);
    private sealed record SnapshotRoot(string Id, bool Exists);
    private sealed record SnapshotEntry(
        string RootId,
        string RelativePath,
        SnapshotEntryKind Kind,
        long Length,
        string? Sha256,
        long LastWriteUtcTicks,
        int Attributes);
    private sealed record SnapshotManifest(
        int SchemaVersion,
        string SnapshotId,
        string OwnerSid,
        DateTimeOffset CreatedUtc,
        UserStateSnapshotLimits Limits,
        IReadOnlyList<SnapshotRoot> Roots,
        IReadOnlyList<SnapshotEntry> Entries);
    private sealed record SnapshotCommit(UserStateSnapshotHandle Handle);
    private sealed record CommittedSnapshot(string RootPath, SnapshotManifest Manifest);
}

internal static class WindowsAlternateStreamGuard
{
    private const int ErrorHandleEof = 38;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorFileNotFound = 2;

    public static void ThrowIfNamedStreamsExist(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var data = new Win32FindStreamData();
        using var handle = FindFirstStreamW(path, 0, data, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorHandleEof or ErrorInvalidParameter or ErrorFileNotFound) return;
            throw new UserStateSnapshotException("Unable to inspect alternate data streams for a user-state file.");
        }

        do
        {
            if (!string.Equals(data.StreamName, "::$DATA", StringComparison.OrdinalIgnoreCase))
            {
                throw new UserStateSnapshotException("Named alternate data streams are not copied; snapshot refused to avoid silent data loss.");
            }
        }
        while (FindNextStreamW(handle, data));

        var lastError = Marshal.GetLastWin32Error();
        if (lastError != ErrorHandleEof)
        {
            throw new UserStateSnapshotException("Unable to finish alternate data stream inspection.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class Win32FindStreamData
    {
        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string StreamName = string.Empty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindHandle FindFirstStreamW(
        string fileName,
        int infoLevel,
        [In, Out] Win32FindStreamData findStreamData,
        int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(SafeFindHandle findStream,
        [In, Out] Win32FindStreamData findStreamData);

    private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFindHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
