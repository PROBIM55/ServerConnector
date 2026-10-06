using Connector.Upgrade.Core;
using Connector.Upgrade.MachineIpc;
using Connector.Upgrade.MachineJournal;
using System.Text.Json.Serialization;

namespace Connector.Upgrade.MachineDispatcher;

public enum MachineDispatcherStatus
{
    Completed = 0,
    Blocked = 1,
    Cancelled = 2,
}

public enum MachineDispatcherCode
{
    None = 0,
    UnsupportedOperation = 1,
    InvalidRequest = 2,
    JournalOwnershipMismatch = 3,
    AssessmentRequired = 4,
    PreparationAlreadyStarted = 5,
    JournalFailure = 6,
    Cancelled = 7,
    OperationFailed = 8,
    ReconciliationRequired = 9,
    NeedsManualRecovery = 10,
    InvalidMutationPlan = 11,
    UnsafeNetBirdOwnership = 12,
    MutationAlreadyApplied = 13,
    VelopackStageUnavailable = 14,
    UserStateStageUnavailable = 15,
}

/// <summary>A bounded status envelope; it deliberately carries no machine paths, secrets, or journal identity.</summary>
public sealed record MachineDispatcherResult(
    int Version,
    Guid CorrelationId,
    MachineIpcOperation Operation,
    MachineDispatcherStatus Status,
    MachineDispatcherCode Code,
    MachineUpgradeState? State = null,
    MachineUpgradePhase? Phase = null,
    long? Revision = null,
    Connector.Upgrade.Core.NetBirdOwnership? NetBirdOwnership = null,
    Connector.Upgrade.Core.NetBirdChangeKind? NetBirdChange = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NetBirdInterruptedRecoveryAction? ReconciliationAction = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReconciliationEvidenceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool RebootRequired = false);

/// <summary>Small seam around the protected store so dispatcher policy can be tested without touching the machine journal.</summary>
public interface IMachineUpgradeJournal : IDisposable
{
    ValueTask<IAsyncDisposable> AcquireLeaseAsync(CancellationToken cancellationToken = default);

    ValueTask<MachineUpgradeJournalDocument?> LoadAsync(
        Guid operationId,
        string initiatingSid,
        CancellationToken cancellationToken = default);

    ValueTask InitializeAsync(
        MachineUpgradeJournalDocument document,
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        Guid operationId,
        string initiatingSid,
        long expectedRevision,
        MachineUpgradeJournalDocument next,
        CancellationToken cancellationToken = default);

    ValueTask<MachineUpgradeJournalDocument> RolloverAsync(
        Guid previousOperationId, MachineUpgradeJournalDocument next, string initiatingSid,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MachineUpgradeJournalDocument>(new NotSupportedException("Journal rollover is not supported."));
}

/// <summary>Production adapter preserving the real store's trusted owner and lease requirements.</summary>
public sealed class MachineUpgradeJournalAdapter(MachineUpgradeJournalStore store) : IMachineUpgradeJournal
{
    private readonly MachineUpgradeJournalStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public ValueTask<IAsyncDisposable> AcquireLeaseAsync(CancellationToken cancellationToken = default) =>
        _store.AcquireLeaseAsync(cancellationToken);

    public ValueTask<MachineUpgradeJournalDocument?> LoadAsync(
        Guid operationId,
        string initiatingSid,
        CancellationToken cancellationToken = default) =>
        _store.LoadAsync(operationId, initiatingSid, cancellationToken);

    public ValueTask InitializeAsync(
        MachineUpgradeJournalDocument document,
        CancellationToken cancellationToken = default) =>
        _store.InitializeAsync(document, cancellationToken);

    public ValueTask SaveAsync(
        Guid operationId,
        string initiatingSid,
        long expectedRevision,
        MachineUpgradeJournalDocument next,
        CancellationToken cancellationToken = default) =>
        _store.SaveAsync(operationId, initiatingSid, expectedRevision, next, cancellationToken);

    public ValueTask<MachineUpgradeJournalDocument> RolloverAsync(
        Guid previousOperationId, MachineUpgradeJournalDocument next, string initiatingSid,
        CancellationToken cancellationToken = default) =>
        _store.RolloverAsync(previousOperationId, next, initiatingSid, cancellationToken);

    public void Dispose() => _store.Dispose();
}
