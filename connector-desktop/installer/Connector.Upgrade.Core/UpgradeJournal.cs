using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connector.Upgrade.Core;

public enum UpgradeJournalState
{
    InProgress,
    RollingBack,
    RolledBack,
    Committed,
    NeedsManualRecovery,
}

public enum UpgradePhase
{
    Preflight = 10,
    EnsureNetBird = 20,
    InstallUnifiedApplication = 30,
    EnrollPlatform = 40,
    VerifyNewAccess = 50,
    RemoveStructuraConnector = 60,
    RemovePlatformConnector = 70,
    VerifyFinalState = 80,
    Commit = 90,
    Rollback = 100,
}

public enum UpgradeMutation
{
    AcquireStructuraRollbackPayload,
    AcquirePlatformRollbackPayload,
    EnsureNetBird,
    InstallUnifiedApplication,
    EnrollPlatform,
    RemoveStructuraConnector,
    RemovePlatformConnector,
    RestorePlatformConnector,
    RestoreStructuraConnector,
    RemoveNewEnrollment,
    RemoveUnifiedApplication,
    RollbackNetBird,
    RestoreUserState,
}

public enum UpgradeJournalEventKind
{
    PhaseEntered,
    MutationIntent,
    MutationPrepared,
    MutationApplied,
    MutationCompensated,
    MutationFailed,
    ObservationPassed,
    SkippedForeignComponent,
}

public sealed record UpgradeJournalEvent(
    long Sequence,
    UpgradeJournalEventKind Kind,
    UpgradePhase Phase,
    UpgradeMutation? Mutation,
    DateTimeOffset RecordedAtUtc,
    string? Code = null);

public sealed record UpgradeJournalDocument(
    int SchemaVersion,
    Guid RunId,
    long Revision,
    UpgradeJournalState State,
    UpgradePhase Phase,
    List<UpgradeJournalEvent> Events,
    string? FailureCode = null,
    UpgradeRecoveryMetadata? Recovery = null)
{
    public const int CurrentSchemaVersion = 6;

    [JsonIgnore]
    public bool PreserveOriginalBytes { get; init; }

    public static UpgradeJournalDocument? AsManualRecoveryIfUnsupportedInterruptedSchema(
        UpgradeJournalDocument journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.SchemaVersion is not (4 or 5))
            return null;

        var compatible = journal with
        {
            SchemaVersion = CurrentSchemaVersion,
            Recovery = null,
            PreserveOriginalBytes = true,
        };
        return journal.State is UpgradeJournalState.InProgress or UpgradeJournalState.RollingBack
            ? compatible with
            {
                State = UpgradeJournalState.NeedsManualRecovery,
                FailureCode = $"journal_schema_v{journal.SchemaVersion}_requires_manual_recovery",
            }
            : compatible;
    }
}

public sealed record UpgradeRecoveryMetadata(
    UpgradePreflight Preflight,
    List<ProtectedRollbackPayloadReceipt> ProtectedRollbackPayloads,
    NetBirdOperationIntent? NetBirdPlan,
    NetBirdOperationReceipt? NetBirdReceipt,
    UnifiedApplicationReceipt? UnifiedApplicationReceipt,
    PlatformEnrollmentReceipt? PlatformEnrollmentReceipt,
    List<LegacyRemovalReceipt> LegacyRemovalReceipts);

public sealed record ProtectedRollbackPayloadReceipt(
    string HandleId,
    LegacyApplicationKind Kind,
    RollbackPayloadProtection Protection);

public interface IUpgradeJournalLease : IAsyncDisposable
{
}

public interface IUpgradeJournalStore
{
    ValueTask<IUpgradeJournalLease> AcquireLeaseAsync(CancellationToken cancellationToken);
    ValueTask<UpgradeJournalDocument?> LoadAsync(CancellationToken cancellationToken);
    ValueTask SaveAsync(UpgradeJournalDocument journal, CancellationToken cancellationToken);
}

public sealed class UpgradeLeaseUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>Atomic, write-through JSON journal. A non-terminal intent is deliberately not replayed.</summary>
public sealed class FileUpgradeJournalStore : IUpgradeJournalStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileUpgradeJournalStore(string path)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Journal path is required.", nameof(path))
            : Path.GetFullPath(path);
    }

    public ValueTask<IUpgradeJournalLease> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        try
        {
            var stream = new FileStream(
                _path + ".lease",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            return ValueTask.FromResult<IUpgradeJournalLease>(new FileUpgradeJournalLease(stream));
        }
        catch (IOException error)
        {
            throw new UpgradeLeaseUnavailableException("Another upgrade process holds the machine journal lease.", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new UpgradeLeaseUnavailableException("The machine journal lease cannot be acquired.", error);
        }
    }

    public async ValueTask<UpgradeJournalDocument?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(UpgradeJournalDocument journal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.SchemaVersion != UpgradeJournalDocument.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported journal schema {journal.SchemaVersion}.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (current?.PreserveOriginalBytes == true)
                throw new InvalidDataException("The legacy journal is preserved for manual recovery or terminal reporting.");
            if (current is null)
            {
                if (journal.Revision != 0)
                    throw new InvalidOperationException("The first journal revision must be zero.");
            }
            else if (current.RunId != journal.RunId || journal.Revision != current.Revision + 1)
            {
                throw new InvalidOperationException("Journal compare-and-swap revision mismatch.");
            }

            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, journal, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async ValueTask<UpgradeJournalDocument?> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var journal = await JsonSerializer.DeserializeAsync<UpgradeJournalDocument>(
                stream,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Upgrade journal is empty.");
        if (journal.SchemaVersion != UpgradeJournalDocument.CurrentSchemaVersion)
        {
            var manualRecovery = UpgradeJournalDocument.AsManualRecoveryIfUnsupportedInterruptedSchema(journal);
            if (manualRecovery is not null) return manualRecovery;
            throw new InvalidDataException($"Unsupported journal schema {journal.SchemaVersion}.");
        }
        return journal;
    }

    private sealed class FileUpgradeJournalLease(FileStream stream) : IUpgradeJournalLease
    {
        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
