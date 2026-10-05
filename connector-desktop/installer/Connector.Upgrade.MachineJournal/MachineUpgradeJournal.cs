using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Connector.Upgrade.Core;
using Connector.Upgrade.MachinePipe;
using Connector.Upgrade.WindowsJournal;

namespace Connector.Upgrade.MachineJournal;

public enum MachineUpgradeState
{
    InProgress = 0,
    RollingBack = 1,
    RolledBack = 2,
    Committed = 3,
    NeedsManualRecovery = 4,
}

public enum MachineUpgradePhase
{
    Preflight = 10,
    AssessNetBird = 20,
    PrepareNetBirdMutation = 30,
    MutateNetBird = 40,
    InstallVelopack = 50,
    Verify = 60,
    Rollback = 70,
    Complete = 80,
    RecoveryRollback = 90,
}

public enum MachineUpgradeRolloverPhase { Prepared = 1, Committed = 2 }

public sealed record MachineUpgradeRolloverRecord(
    int SchemaVersion, Guid PreviousOperationId, Guid NextOperationId, string InitiatingSid,
    MachineUpgradeRolloverPhase Phase, string PreviousJournalSha256);

/// <summary>Only opaque handles and stable package evidence may cross the journal boundary.</summary>
public sealed record ProtectedVelopackReceiptIdentifiers(
    string? SetupHandleId,
    string? InstallReceiptId);

/// <summary>Opaque rollback MSI handles only; MSI paths, pins, hashes, and tokens stay elsewhere.</summary>
public sealed record ProtectedRollbackPayloadReceiptIdentifiers(
    string? StructuraConnectorHandleId = null,
    string? PlatformConnectorHandleId = null);

/// <summary>Rich recovery evidence retained only in the protected machine journal.</summary>
public sealed record NetBirdRecoveryReceipt(
    NetBirdChangeKind Change,
    string? OperationId,
    string? PriorRestoreHandleId,
    RollbackPayloadProtection? PriorRestoreProtection,
    NetBirdOwnedState? PriorOwnedState,
    NetBirdOwnedState? ResultingOwnedState,
    NetBirdMsiPackageIdentity? PriorPackage = null,
    string? PriorInstallerSha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool RebootRequired = false);

/// <summary>Bounded proof that interrupted NetBird work was classified by the protected host port.</summary>
public sealed record NetBirdReconciliationReceipt(
    NetBirdInterruptedRecoveryAction Action,
    string EvidenceId);

public enum MachineNetBirdCompensationAction
{
    RemoveInstalledThisRun = 1,
    RestoreUpdatedThisRun = 2,
}

/// <summary>Protected compensation intent; Completed is written only after the machine port returns.</summary>
public sealed record MachineNetBirdCompensation(
    MachineNetBirdCompensationAction Action,
    bool Completed,
    string? EvidenceId = null);

/// <summary>
/// Durable state for the single active machine installer operation. It intentionally contains no
/// source or staging paths and no Platform or Structura application token.
/// </summary>
public sealed record MachineUpgradeJournalDocument(
    int SchemaVersion,
    Guid OperationId,
    string InitiatingSid,
    long Revision,
    MachineUpgradeState State,
    MachineUpgradePhase Phase,
    NetBirdAssessment? NetBirdAssessment,
    NetBirdMutationPlan? NetBirdMutationPlan,
    NetBirdRecoveryReceipt? RecoveryReceipt,
    ProtectedVelopackReceiptIdentifiers VelopackReceipts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NetBirdReconciliationReceipt? NetBirdReconciliation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    MachineNetBirdCompensation? NetBirdCompensation = null,
    // Schema 1 was not released before this optional field was introduced. Existing schema-1
    // journals without it deserialize as null; unknown schema versions remain fail-closed.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ProtectedRollbackPayloadReceiptIdentifiers? RollbackPayloadReceipts = null)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Protected, single-operation, write-through journal. The operation and SID supplied to reads and
/// updates must match the durable owner record; each update is a revision compare-and-swap.
/// </summary>
public sealed class MachineUpgradeJournalStore : IDisposable
{
    private const int MaximumDocumentBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _rootPath;
    private readonly string _journalPath;
    private readonly string _leasePath;
    private readonly string _rolloverPath;
    private readonly WindowsJournalSecurityProfile _security;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _lease;
    private int _disposed;

    public MachineUpgradeJournalStore()
        : this(GetDefaultRoot(), WindowsJournalSecurity.ProductionProfile)
    {
    }

    internal MachineUpgradeJournalStore(string rootPath, WindowsJournalSecurityProfile security)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The machine upgrade journal requires Windows.");
        _security = security ?? throw new ArgumentNullException(nameof(security));
        WindowsJournalSecurity.RequireTrustedElevatedCaller(_security);
        _rootPath = Path.GetFullPath(rootPath ?? throw new ArgumentNullException(nameof(rootPath)));
        _journalPath = Path.Combine(_rootPath, "machine-journal.json");
        _leasePath = Path.Combine(_rootPath, "machine-journal.lease");
        _rolloverPath = Path.Combine(_rootPath, "machine-journal.rollover.json");
        EnsureRoot();
    }

    public async ValueTask<IAsyncDisposable> AcquireLeaseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_lease is not null)
                throw new InvalidOperationException("This machine journal store already owns its lease.");
            EnsureRoot();
            FileStream stream;
            if (File.Exists(_leasePath))
            {
                WindowsJournalSecurity.ValidateProtectedFile(_leasePath, _security);
                stream = new FileStream(_leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.WriteThrough);
            }
            else
            {
                stream = WindowsJournalSecurity.CreateProtectedFile(_leasePath, FileAccess.ReadWrite,
                    FileShare.None, FileOptions.WriteThrough, _security);
            }
            WindowsJournalSecurity.ValidateProtectedFile(_leasePath, _security);
            EnsureRoot();
            _lease = stream;
            return new Lease(this, stream);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("Another installer process owns the machine journal lease.", error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<MachineUpgradeJournalDocument?> LoadAsync(
        Guid operationId,
        string initiatingSid,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireLease();
            EnsureRoot();
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            await RequireNoPreparedRolloverAsync(cancellationToken).ConfigureAwait(false);
            if (current is not null)
                RequireOwner(current, operationId, initiatingSid);
            return current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask InitializeAsync(
        MachineUpgradeJournalDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateDocument(document);
        if (document.Revision != 0 || document.State != MachineUpgradeState.InProgress ||
            document.Phase != MachineUpgradePhase.Preflight)
            throw new InvalidDataException("A new machine operation must start at revision zero in Preflight.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireLease();
            EnsureRoot();
            await RequireNoPreparedRolloverAsync(cancellationToken).ConfigureAwait(false);
            if (await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("A machine operation is already journaled; it must be recovered first.");
            await WriteUnlockedAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(
        Guid operationId,
        string initiatingSid,
        long expectedRevision,
        MachineUpgradeJournalDocument next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        ValidateDocument(next);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireLease();
            EnsureRoot();
            await RequireNoPreparedRolloverAsync(cancellationToken).ConfigureAwait(false);
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The machine operation has not been initialized.");
            RequireOwner(current, operationId, initiatingSid);
            if (current.Revision != expectedRevision || next.Revision != expectedRevision + 1)
                throw new InvalidOperationException("Machine journal compare-and-swap revision mismatch.");
            ValidateTransition(current, next);
            await WriteUnlockedAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Archives a fully rolled-back operation and publishes its successor, resuming a prepared rollover after interruption.</summary>
    public async ValueTask<MachineUpgradeJournalDocument> RolloverAsync(
        Guid previousOperationId, MachineUpgradeJournalDocument next, string initiatingSid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        ValidateSid(initiatingSid);
        ValidateDocument(next);
        if (previousOperationId == Guid.Empty || previousOperationId == next.OperationId ||
            next.InitiatingSid != initiatingSid || next.Revision != 0 || next.State != MachineUpgradeState.InProgress ||
            next.Phase != MachineUpgradePhase.Preflight || next.NetBirdAssessment is not null ||
            next.NetBirdMutationPlan is not null || next.RecoveryReceipt is not null ||
            next.NetBirdReconciliation is not null || next.NetBirdCompensation is not null ||
            next.RollbackPayloadReceipts is not null || next.VelopackReceipts.SetupHandleId is not null ||
            next.VelopackReceipts.InstallReceiptId is not null)
            throw new InvalidDataException("A rollover target must be a pristine new operation for the same SID.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireLease();
            EnsureRoot();
            var record = await ReadRolloverUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (record is not null && (record.PreviousOperationId != previousOperationId ||
                record.NextOperationId != next.OperationId || record.InitiatingSid != initiatingSid))
            {
                if (record.Phase != MachineUpgradeRolloverPhase.Committed || current?.OperationId != previousOperationId ||
                    current.InitiatingSid != initiatingSid || current.State != MachineUpgradeState.RolledBack)
                    throw new InvalidDataException("A different or corrupted machine rollover record blocks this request.");
                record = null;
            }
            if (record is null)
            {
                if (current is null || current.OperationId != previousOperationId ||
                    current.InitiatingSid != initiatingSid || current.State != MachineUpgradeState.RolledBack)
                    throw new UnauthorizedAccessException("Only the authenticated owner of a fully rolled-back operation may roll over.");
                var previousJournalSha256 = await ComputeProtectedFileSha256Async(_journalPath, cancellationToken)
                    .ConfigureAwait(false);
                record = new MachineUpgradeRolloverRecord(1, previousOperationId, next.OperationId, initiatingSid,
                    MachineUpgradeRolloverPhase.Prepared, previousJournalSha256);
                await WriteRolloverUnlockedAsync(record, replace: File.Exists(_rolloverPath), cancellationToken).ConfigureAwait(false);
            }
            else if (record.Phase == MachineUpgradeRolloverPhase.Committed)
            {
                if (current?.OperationId != next.OperationId || current.InitiatingSid != initiatingSid)
                    throw new UnauthorizedAccessException("The completed rollover no longer names the active operation.");
                var committedArchivePath = Path.Combine(_rootPath, $"machine-journal-{previousOperationId:N}.json");
                var committedArchiveHash = await ComputeProtectedFileSha256Async(committedArchivePath, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(committedArchiveHash, record.PreviousJournalSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("The committed rollover archive no longer matches its prepared journal.");
                return current;
            }

            var archivePath = Path.Combine(_rootPath, $"machine-journal-{previousOperationId:N}.json");
            if (!File.Exists(archivePath))
            {
                if (current?.OperationId != previousOperationId || current.State != MachineUpgradeState.RolledBack)
                    throw new InvalidDataException("Prepared rollover has neither its old active journal nor archive.");
                var activeHash = await ComputeProtectedFileSha256Async(_journalPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(activeHash, record.PreviousJournalSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("The active rolled-back journal changed after rollover was prepared.");
                await CopyProtectedFileDurablyAsync(_journalPath, archivePath, cancellationToken).ConfigureAwait(false);
            }
            var archiveHash = await ComputeProtectedFileSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(archiveHash, record.PreviousJournalSha256, StringComparison.Ordinal))
                throw new InvalidDataException("The immutable archive does not match the journal bound by the prepared rollover.");
            var archived = await ReadDocumentAtPathAsync(archivePath, cancellationToken).ConfigureAwait(false);
            if (archived.OperationId != previousOperationId || archived.InitiatingSid != initiatingSid ||
                archived.State != MachineUpgradeState.RolledBack)
                throw new InvalidDataException("The immutable archived journal does not match the rollover owner.");
            if (current?.OperationId == previousOperationId && archived != current)
                throw new InvalidDataException("The existing immutable archive differs from the active rolled-back journal.");

            if (current?.OperationId == previousOperationId)
                await WriteUnlockedAsync(next, cancellationToken).ConfigureAwait(false);
            else if (current?.OperationId != next.OperationId || current.InitiatingSid != initiatingSid)
                throw new InvalidDataException("Prepared rollover active journal does not match either operation.");

            record = record with { Phase = MachineUpgradeRolloverPhase.Committed };
            await WriteRolloverUnlockedAsync(record, replace: true, cancellationToken).ConfigureAwait(false);
            return (await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false))!;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _lease?.Dispose();
        _lease = null;
    }

    private async ValueTask<MachineUpgradeJournalDocument?> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
            return null;
        WindowsJournalSecurity.ValidateProtectedFile(_journalPath, _security);
        await using var stream = new FileStream(_journalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        WindowsJournalSecurity.ValidateProtectedFile(_journalPath, _security);
        if (stream.Length is <= 0 or > MaximumDocumentBytes)
            throw new InvalidDataException("The machine journal is empty or exceeds its size limit.");
        var document = await JsonSerializer.DeserializeAsync<MachineUpgradeJournalDocument>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The machine journal is empty.");
        ValidateDocument(document);
        return document;
    }

    private async ValueTask<MachineUpgradeRolloverRecord?> ReadRolloverUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_rolloverPath)) return null;
        WindowsJournalSecurity.ValidateProtectedFile(_rolloverPath, _security);
        await using var stream = new FileStream(_rolloverPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 4096) throw new InvalidDataException("The machine rollover record is invalid.");
        var record = await JsonSerializer.DeserializeAsync<MachineUpgradeRolloverRecord>(stream, JsonOptions,
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("The machine rollover record is empty.");
        ValidateRolloverRecord(record);
        return record;
    }

    private async ValueTask<MachineUpgradeJournalDocument> ReadDocumentAtPathAsync(string path, CancellationToken cancellationToken)
    {
        WindowsJournalSecurity.ValidateProtectedFile(path, _security);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumDocumentBytes)
            throw new InvalidDataException("The archived machine journal is empty or exceeds its size limit.");
        var document = await JsonSerializer.DeserializeAsync<MachineUpgradeJournalDocument>(stream, JsonOptions,
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("The archived machine journal is empty.");
        ValidateDocument(document);
        return document;
    }

    private async ValueTask<string> ComputeProtectedFileSha256Async(string path, CancellationToken cancellationToken)
    {
        WindowsJournalSecurity.ValidateProtectedFile(path, _security);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumDocumentBytes)
            throw new InvalidDataException("The protected journal file is empty or exceeds its size limit.");
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask WriteRolloverUnlockedAsync(MachineUpgradeRolloverRecord record, bool replace,
        CancellationToken cancellationToken)
    {
        ValidateRolloverRecord(record);
        var temporary = Path.Combine(_rootPath, $".machine-rollover.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = WindowsJournalSecurity.CreateProtectedFile(temporary, FileAccess.Write,
                FileShare.None, FileOptions.Asynchronous | FileOptions.WriteThrough, _security))
            {
                await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            WindowsJournalSecurity.ValidateProtectedFile(temporary, _security);
            var flags = MoveFileFlags.WriteThrough | (replace ? MoveFileFlags.ReplaceExisting : 0);
            if (!MoveFileEx(temporary, _rolloverPath, flags))
                throw new IOException("The machine rollover record could not be committed durably.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            temporary = string.Empty;
            WindowsJournalSecurity.ValidateProtectedFile(_rolloverPath, _security);
        }
        finally { if (temporary.Length > 0 && File.Exists(temporary)) File.Delete(temporary); }
    }

    private async ValueTask CopyProtectedFileDurablyAsync(string sourcePath, string archivePath,
        CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(_rootPath, $".machine-archive.{Guid.NewGuid():N}.tmp");
        try
        {
            WindowsJournalSecurity.ValidateProtectedFile(sourcePath, _security);
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = WindowsJournalSecurity.CreateProtectedFile(temporary, FileAccess.Write,
                FileShare.None, FileOptions.Asynchronous | FileOptions.WriteThrough, _security))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }
            WindowsJournalSecurity.ValidateProtectedFile(temporary, _security);
            if (!MoveFileEx(temporary, archivePath, MoveFileFlags.WriteThrough))
                throw new IOException("The immutable machine journal archive could not be created.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            temporary = string.Empty;
            WindowsJournalSecurity.ValidateProtectedFile(archivePath, _security);
        }
        finally { if (temporary.Length > 0 && File.Exists(temporary)) File.Delete(temporary); }
    }

    private async ValueTask RequireNoPreparedRolloverAsync(CancellationToken cancellationToken)
    {
        var record = await ReadRolloverUnlockedAsync(cancellationToken).ConfigureAwait(false);
        if (record?.Phase == MachineUpgradeRolloverPhase.Prepared)
            throw new InvalidOperationException("A prepared machine rollover must be resumed before any other operation.");
    }

    private static void ValidateRolloverRecord(MachineUpgradeRolloverRecord record)
    {
        if (record.SchemaVersion != 1 || record.PreviousOperationId == Guid.Empty ||
            record.NextOperationId == Guid.Empty || record.PreviousOperationId == record.NextOperationId ||
            !Enum.IsDefined(record.Phase) || record.PreviousJournalSha256 is null ||
            record.PreviousJournalSha256.Length != 64 || !record.PreviousJournalSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The machine rollover record is malformed.");
        ValidateSid(record.InitiatingSid);
    }

    private async ValueTask WriteUnlockedAsync(
        MachineUpgradeJournalDocument document,
        CancellationToken cancellationToken)
    {
        string? temporary = Path.Combine(_rootPath, $".machine-journal.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = WindowsJournalSecurity.CreateProtectedFile(temporary, FileAccess.Write,
                             FileShare.None, FileOptions.Asynchronous | FileOptions.WriteThrough, _security))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            WindowsJournalSecurity.ValidateProtectedFile(temporary, _security);
            EnsureRoot();
            if (File.Exists(_journalPath))
                WindowsJournalSecurity.ValidateProtectedFile(_journalPath, _security);
            ReplaceFileDurably(temporary, _journalPath);
            temporary = null;
            WindowsJournalSecurity.ValidateProtectedFile(_journalPath, _security);
            EnsureRoot();
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void EnsureRoot()
    {
        if (!Directory.Exists(_rootPath))
        {
            var parent = Directory.GetParent(_rootPath)?.FullName
                ?? throw new InvalidDataException("The machine journal root has no parent directory.");
            if (!Directory.Exists(parent))
                throw new DirectoryNotFoundException("The machine journal parent directory is missing.");
            new DirectoryInfo(_rootPath).Create(WindowsJournalSecurity.CreateProtectedDirectoryAcl(_security));
        }
        if (File.Exists(_rootPath))
            throw new InvalidDataException("The machine journal root is a file.");
        WindowsJournalSecurity.ValidateProtectedDirectory(_rootPath, _security);
    }

    private void RequireLease()
    {
        ThrowIfDisposed();
        if (_lease is null || _lease.SafeFileHandle.IsClosed)
            throw new InvalidOperationException("Machine journal reads and writes require an active lease.");
    }

    private static void RequireOwner(MachineUpgradeJournalDocument current, Guid operationId, string initiatingSid)
    {
        ValidateSid(initiatingSid);
        if (operationId == Guid.Empty || current.OperationId != operationId ||
            !string.Equals(current.InitiatingSid, initiatingSid, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The requested operation or initiating SID does not own this journal.");
    }

    private static void ValidateDocument(MachineUpgradeJournalDocument document)
    {
        if (document.SchemaVersion != MachineUpgradeJournalDocument.CurrentSchemaVersion ||
            document.OperationId == Guid.Empty || document.Revision < 0 || document.VelopackReceipts is null ||
            !Enum.IsDefined(document.State) || !Enum.IsDefined(document.Phase))
            throw new InvalidDataException("The machine journal has invalid identity, version, revision, state, or phase.");
        ValidateSid(document.InitiatingSid);
        ValidateVelopackSetupHandle(document.VelopackReceipts.SetupHandleId);
        ValidateOperationIdentifier(document.VelopackReceipts.InstallReceiptId, "Install receipt");
        ValidateRollbackPayloadReceipts(document);
        ValidateNetBird(document.NetBirdAssessment, document.NetBirdMutationPlan, document.RecoveryReceipt,
            document.OperationId);
        ValidateReconciliation(document.NetBirdMutationPlan, document.NetBirdReconciliation);
        ValidateCompensation(document);
        if (document.NetBirdReconciliation is not null &&
            (document.RecoveryReceipt is not null || document.State is not (MachineUpgradeState.RollingBack or MachineUpgradeState.RolledBack or MachineUpgradeState.NeedsManualRecovery) ||
             document.Phase is not (MachineUpgradePhase.Rollback or MachineUpgradePhase.RecoveryRollback)))
            throw new InvalidDataException("A reconciled NetBird result must be exclusive and belong to rollback state.");
    }

    private static void ValidateCompensation(MachineUpgradeJournalDocument document)
    {
        var compensation = document.NetBirdCompensation;
        if (compensation is null) return;
        if (!Enum.IsDefined(compensation.Action) || document.RecoveryReceipt is null ||
            document.NetBirdMutationPlan is null || document.NetBirdAssessment is null ||
            (compensation.Action, document.RecoveryReceipt.Change) is not
                (MachineNetBirdCompensationAction.RemoveInstalledThisRun, NetBirdChangeKind.InstalledThisRun) and not
                (MachineNetBirdCompensationAction.RestoreUpdatedThisRun, NetBirdChangeKind.UpdatedThisRun))
            throw new InvalidDataException("The NetBird compensation intent is not bound to a durable Apply receipt.");
        if (compensation.EvidenceId is { } evidence && !IsSafeNetBirdEvidenceId(evidence))
            throw new InvalidDataException("The NetBird compensation evidence is invalid.");

        var rollbackPhase = document.Phase is MachineUpgradePhase.Rollback or MachineUpgradePhase.RecoveryRollback;
        if (!rollbackPhase || (!compensation.Completed && document.State is not
                (MachineUpgradeState.RollingBack or MachineUpgradeState.NeedsManualRecovery)) ||
            (compensation.Completed && document.State != MachineUpgradeState.RolledBack) ||
            (!compensation.Completed && compensation.EvidenceId is not null))
            throw new InvalidDataException("The NetBird compensation state is inconsistent with rollback state.");
    }

    private static void ValidateRollbackPayloadReceipts(MachineUpgradeJournalDocument document)
    {
        var receipts = document.RollbackPayloadReceipts;
        if (receipts is null) return;
        var structura = receipts.StructuraConnectorHandleId;
        var platform = receipts.PlatformConnectorHandleId;
        if (structura is null && platform is null)
            throw new InvalidDataException("An empty rollback MSI receipt set is invalid.");
        if (structura is not null && !string.Equals(
                structura,
                FormatRollbackPayloadHandle(document.OperationId, LegacyApplicationKind.StructuraConnector),
                StringComparison.Ordinal))
            throw new InvalidDataException("The Structura rollback MSI handle is malformed or belongs to another operation.");
        if (platform is not null && !string.Equals(
                platform,
                FormatRollbackPayloadHandle(document.OperationId, LegacyApplicationKind.PlatformConnector),
                StringComparison.Ordinal))
            throw new InvalidDataException("The Platform rollback MSI handle is malformed or belongs to another operation.");
        if (structura is not null && string.Equals(structura, platform, StringComparison.Ordinal))
            throw new InvalidDataException("Rollback MSI receipt handles must be unique.");
        if (document.Phase == MachineUpgradePhase.Preflight || document.NetBirdAssessment is null)
            throw new InvalidDataException("Rollback MSI handles require an inspected operation in AssessNetBird or a later phase.");
    }

    private static string FormatRollbackPayloadHandle(Guid operationId, LegacyApplicationKind kind) =>
        $"windows-msi-op-v1:{operationId:N}:{(int)kind}";

    private static void ValidateReconciliation(NetBirdMutationPlan? plan, NetBirdReconciliationReceipt? receipt)
    {
        if (receipt is null) return;
        if (plan is null || !Enum.IsDefined(receipt.Action))
            throw new InvalidDataException("A NetBird reconciliation receipt requires a plan and known action.");
        var validAction = (plan.Change, receipt.Action) switch
        {
            (NetBirdChangeKind.NoChange, NetBirdInterruptedRecoveryAction.NoMutationObserved) => true,
            (NetBirdChangeKind.InstalledThisRun, NetBirdInterruptedRecoveryAction.NoMutationObserved or
                NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun) => true,
            (NetBirdChangeKind.UpdatedThisRun, NetBirdInterruptedRecoveryAction.NoMutationObserved or
                NetBirdInterruptedRecoveryAction.RestoredPriorOwnedState) => true,
            _ => false,
        };
        if (!validAction || string.IsNullOrWhiteSpace(receipt.EvidenceId) || receipt.EvidenceId.Length > 256 ||
            !IsSafeNetBirdEvidenceId(receipt.EvidenceId))
            throw new InvalidDataException("The NetBird reconciliation receipt action or evidence is invalid.");
    }

    private static bool IsSafeNetBirdEvidenceId(string? value)
    {
        const string prefix = "netbird-windows-v1:";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('|');
        return parts.Length is 1 or 2 && parts.All(part =>
            part.StartsWith(prefix, StringComparison.Ordinal) &&
            part.Length == prefix.Length + 64 &&
            part.AsSpan(prefix.Length).ToString().All(Uri.IsHexDigit));
    }

    private static void ValidateNetBird(
        NetBirdAssessment? assessment,
        NetBirdMutationPlan? plan,
        NetBirdRecoveryReceipt? receipt,
        Guid operationId)
    {
        if (assessment is not null)
        {
            if (!Enum.IsDefined(assessment.Ownership))
                throw new InvalidDataException("The NetBird assessment has an unknown ownership value.");
            ValidateOperationIdentifier(assessment.InstallationId, "NetBird installation id");
            ValidateOwnedState(assessment.OwnedState);
            if ((assessment.Ownership == NetBirdOwnership.OwnedByConnector) != (assessment.OwnedState is not null))
                throw new InvalidDataException("Only a Connector-owned NetBird assessment may include owned state.");
        }
        if (plan is not null)
        {
            if (!Enum.IsDefined(plan.Change) || plan.PriorAssessment is null || plan.TargetPackage is null ||
                plan.TargetPackage.Package is null ||
                (plan.OperationId is not null && !string.Equals(plan.OperationId, operationId.ToString("D"), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The NetBird mutation plan is incomplete or belongs to another operation.");
            ValidateNetBird(plan.PriorAssessment, null, null, operationId);
            ValidateNetBirdPackageHandle(plan.TargetPackage.HandleId);
            ValidateLeafFileName(plan.TargetPackage.InstallerName, "NetBird installer name");
            ValidateSha256(plan.TargetPackage.Sha256, "NetBird package hash");
            ValidateIdentifier(plan.TargetPackage.SignerSubject, "NetBird package signer", required: true);
            ValidateIdentifier(plan.TargetPackage.SignerThumbprint, "NetBird package signer thumbprint", required: true, maxLength: 128);
            ValidatePackage(plan.TargetPackage.Package);
            ValidateOwnedState(plan.PriorRestorePoint?.OwnedState);
            if (plan.PriorRestorePoint is not null)
            {
                ValidateNetBirdRestoreHandle(plan.PriorRestorePoint.HandleId);
                if (plan.PriorRestorePoint.Package is null)
                    throw new InvalidDataException("The NetBird restore point has no package identity.");
                ValidatePackage(plan.PriorRestorePoint.Package);
                ValidateSha256(plan.PriorRestorePoint.InstallerSha256, "NetBird restore package hash");
            }
        }
        if (receipt is not null)
        {
            if (!Enum.IsDefined(receipt.Change))
                throw new InvalidDataException("The NetBird recovery receipt has an unknown change value.");
            ValidateOperationIdentifier(receipt.OperationId, "NetBird recovery operation");
            if (receipt.OperationId is not null && !string.Equals(receipt.OperationId,
                    operationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The NetBird recovery receipt belongs to another operation.");
            ValidateNetBirdRestoreHandle(receipt.PriorRestoreHandleId, required: false);
            if (receipt.PriorInstallerSha256 is not null)
                ValidateSha256(receipt.PriorInstallerSha256, "NetBird prior installer hash");
            ValidateOwnedState(receipt.PriorOwnedState);
            ValidateOwnedState(receipt.ResultingOwnedState);
            if (receipt.PriorRestoreProtection is { } protection && !Enum.IsDefined(protection))
                throw new InvalidDataException("The NetBird recovery receipt has an unknown protection value.");
            if (receipt.PriorPackage is not null) ValidatePackage(receipt.PriorPackage);
        }
    }

    private static void ValidatePackage(NetBirdMsiPackageIdentity package)
    {
        if (package.ProductCode == Guid.Empty || package.UpgradeCode == Guid.Empty)
            throw new InvalidDataException("A NetBird package identity requires MSI product and upgrade codes.");
        ValidateIdentifier(package.ProductVersion, "NetBird package version", required: true);
        ValidateIdentifier(package.Manufacturer, "NetBird package manufacturer", required: true);
        ValidateIdentifier(package.ProductName, "NetBird package product name", required: true);
    }

    private static void ValidateOwnedState(NetBirdOwnedState? state)
    {
        if (state is null) return;
        ValidateIdentifier(state.InstallationId, "NetBird installation id", required: true);
        ValidateIdentifier(state.Version, "NetBird version", required: true);
        ValidateSha256(state.ConfigurationSha256, "NetBird configuration hash");
        if (state.ServiceIdentity.Length != "netbird-service-v1:".Length + 64 ||
            !state.ServiceIdentity.StartsWith("netbird-service-v1:", StringComparison.Ordinal) ||
            !state.ServiceIdentity["netbird-service-v1:".Length..].All(Uri.IsHexDigit))
            throw new InvalidDataException("The NetBird service identity has an unsupported format.");
    }

    private static void ValidateIdentifier(string? value, string name, bool required, int maxLength = 256)
    {
        if (value is null && !required) return;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl) ||
            value.Contains('/') || value.Contains('\\'))
            throw new InvalidDataException($"The {name} is empty, oversized, or contains a path-like value.");
    }

    private static void ValidateSha256(string? value, string name)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException($"The {name} must be a SHA-256 hex digest.");
    }

    private static void ValidateLeafFileName(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value is "." or ".." ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            value.EndsWith(' ') || value.EndsWith('.') ||
            value.Any(static character => character < 32 || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'))
            throw new InvalidDataException($"The {name} must be a bounded file name without a path.");
        var deviceName = value.Split('.')[0];
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (deviceName.Length == 4 &&
             (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             deviceName[3] is >= '1' and <= '9'))
            throw new InvalidDataException($"The {name} is a reserved Windows device name.");
    }

    private static void ValidateOperationIdentifier(string? value, string name)
    {
        if (value is null) return;
        if (!Guid.TryParseExact(value, "N", out _) && !Guid.TryParseExact(value, "D", out _))
            throw new InvalidDataException($"The {name} must be a GUID operation identifier.");
    }

    private static void ValidateNetBirdPackageHandle(string? value)
    {
        const string prefix = "netbird-msi-v1:sha256:";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            !(value.Length == prefix.Length + 64 || value.Length == prefix.Length + 64 + 39) ||
            !value.AsSpan(prefix.Length, 64).ToString().All(Uri.IsHexDigit) ||
            (value.Length > prefix.Length + 64 &&
             (!value.AsSpan(prefix.Length + 64).StartsWith(":stage:", StringComparison.Ordinal) ||
              !Guid.TryParseExact(value[(prefix.Length + 71)..], "N", out _))))
            throw new InvalidDataException("The NetBird package handle has an unsupported format.");
    }

    private static void ValidateNetBirdRestoreHandle(string? value, bool required = true)
    {
        if (value is null && !required) return;
        const string prefix = "netbird-restore-v1:";
        if (value is null || value.Length != prefix.Length + 32 ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(value[prefix.Length..], "N", out _))
            throw new InvalidDataException("The NetBird restore handle has an unsupported format.");
    }

    private static void ValidateVelopackSetupHandle(string? value)
    {
        if (value is null) return;
        const string prefix = "windows-velopack-setup-v1:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length > 160)
            throw new InvalidDataException("The protected Velopack Setup handle has an unsupported format.");
        var suffix = value[prefix.Length..];
        var parts = suffix.Split(':');
        if (parts.Length is < 1 or > 3 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit) ||
            (parts.Length == 2) || (parts.Length == 3 &&
                (parts[1] != "stage" || !Guid.TryParseExact(parts[2], "N", out _))))
            throw new InvalidDataException("The protected Velopack Setup handle has an unsupported format.");
    }

    private static void ValidateSid(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid) || sid.Length > 184)
            throw new InvalidDataException("A canonical initiating Windows SID is required.");
        SecurityIdentifier parsed;
        try { parsed = new SecurityIdentifier(sid); }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("A canonical initiating Windows SID is required.", error);
        }
        if (!string.Equals(parsed.Value, sid, StringComparison.Ordinal))
            throw new InvalidDataException("A specific Windows user account SID is required.");
        try { _ = MachinePipeIdentity.GetPipeName(Guid.NewGuid(), parsed); }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("A specific Windows user account SID is required.", error);
        }
    }

    private static void ValidateTransition(MachineUpgradeJournalDocument current, MachineUpgradeJournalDocument next)
    {
        if (next.OperationId != current.OperationId || next.InitiatingSid != current.InitiatingSid ||
            next.SchemaVersion != current.SchemaVersion || next.Phase < current.Phase)
            throw new InvalidOperationException("Machine journal identity is immutable and phase cannot move backward.");
        if (!StateTransitionAllowed(current.State, next.State))
            throw new InvalidOperationException("The machine journal state transition is not allowed.");
        ValidateRollbackPayloadReceiptTransition(current, next);
        if (next.State == MachineUpgradeState.RollingBack &&
            next.Phase != (current.State == MachineUpgradeState.NeedsManualRecovery ||
                current.Phase > MachineUpgradePhase.Rollback
                ? MachineUpgradePhase.RecoveryRollback
                : MachineUpgradePhase.Rollback))
            throw new InvalidOperationException("Rollback must enter its matching monotonic rollback phase.");
        if (next.State == MachineUpgradeState.RolledBack &&
            next.Phase is not (MachineUpgradePhase.Rollback or MachineUpgradePhase.RecoveryRollback))
            throw new InvalidOperationException("A rolled back operation must be in a rollback phase.");
        if (next.State == MachineUpgradeState.Committed && next.Phase != MachineUpgradePhase.Complete)
            throw new InvalidOperationException("A committed operation must be in the Complete phase.");
        if (current.State is MachineUpgradeState.Committed or MachineUpgradeState.RolledBack &&
            next != current with { Revision = next.Revision })
            throw new InvalidOperationException("A completed machine operation cannot be modified.");
    }

    private static void ValidateRollbackPayloadReceiptTransition(
        MachineUpgradeJournalDocument current, MachineUpgradeJournalDocument next)
    {
        var before = current.RollbackPayloadReceipts;
        var after = next.RollbackPayloadReceipts;
        var beforeStructura = before?.StructuraConnectorHandleId;
        var beforePlatform = before?.PlatformConnectorHandleId;
        var afterStructura = after?.StructuraConnectorHandleId;
        var afterPlatform = after?.PlatformConnectorHandleId;

        if ((beforeStructura is not null && !string.Equals(beforeStructura, afterStructura, StringComparison.Ordinal)) ||
            (beforePlatform is not null && !string.Equals(beforePlatform, afterPlatform, StringComparison.Ordinal)))
            throw new InvalidOperationException("A durable rollback MSI handle cannot be replaced or removed.");

        var addingReceipt = (beforeStructura is null && afterStructura is not null) ||
                            (beforePlatform is null && afterPlatform is not null);
        if (addingReceipt && (current.State != MachineUpgradeState.InProgress ||
                              current.Phase != MachineUpgradePhase.AssessNetBird ||
                              next.State != MachineUpgradeState.InProgress ||
                              next.Phase != MachineUpgradePhase.AssessNetBird))
            throw new InvalidOperationException("Rollback MSI handles may be staged only while in AssessNetBird.");
    }

    private static bool StateTransitionAllowed(MachineUpgradeState current, MachineUpgradeState next) =>
        current == next || current switch
        {
            MachineUpgradeState.InProgress => next is MachineUpgradeState.RollingBack or MachineUpgradeState.Committed or MachineUpgradeState.NeedsManualRecovery,
            MachineUpgradeState.RollingBack => next is MachineUpgradeState.RolledBack or MachineUpgradeState.NeedsManualRecovery,
            MachineUpgradeState.NeedsManualRecovery => next == MachineUpgradeState.RollingBack,
            _ => false,
        };

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(MachineUpgradeJournalStore));
    }

    private async ValueTask ReleaseLeaseAsync(FileStream stream)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_lease, stream)) _lease = null;
            stream.Dispose();
        }
        finally { _gate.Release(); }
    }

    private static string GetDefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "StructuraConnectorInstaller.MachineJournal");

    private static void ReplaceFileDurably(string sourcePath, string destinationPath)
    {
        const MoveFileFlags flags = MoveFileFlags.ReplaceExisting | MoveFileFlags.WriteThrough;
        if (!MoveFileEx(sourcePath, destinationPath, flags))
            throw new IOException("The machine journal could not be committed atomically.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }

    [Flags]
    private enum MoveFileFlags : uint { ReplaceExisting = 1, WriteThrough = 8 }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, MoveFileFlags flags);

    private sealed class Lease(MachineUpgradeJournalStore owner, FileStream stream) : IAsyncDisposable
    {
        private int _released;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _released, 1) == 0
            ? owner.ReleaseLeaseAsync(stream)
            : ValueTask.CompletedTask;
    }
}
