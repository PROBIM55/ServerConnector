namespace Platform.Connector.Core;

public sealed record StoredTerminalStatus(
    ConnectorJobStatusEnvelope Envelope,
    bool Published,
    DateTime SavedAtUtc,
    string? RemoteAuthority = null,
    ConnectorJobEnvelope? SourceJob = null);

public enum StoredLocalJobState
{
    Queued = 1,
    Running = 2
}

public sealed record StoredLocalJob(
    string DeviceId,
    ConnectorJobEnvelope Job,
    StoredLocalJobState State,
    long Sequence,
    DateTime AcceptedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record LocalJobSnapshot(
    ConnectorJobEnvelope Job,
    ConnectorJobStatusEnvelope LastStatus,
    StoredLocalJobState? PersistedState,
    DateTime SavedAtUtc);

public sealed record LocalRecoverySnapshot(
    string DeviceId,
    IReadOnlyList<LocalJobSnapshot> Jobs);

public sealed record StoredLocalStateSnapshot(
    IReadOnlyList<StoredLocalJob> AcceptedJobs,
    IReadOnlyList<StoredTerminalStatus> TerminalResults);

public interface IRequestIdempotencyStore
{
    Task<StoredTerminalStatus?> TryGetAsync(string requestId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredTerminalStatus>> GetPendingAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<StoredTerminalStatus>>([]);

    Task<IReadOnlyList<StoredTerminalStatus>> GetTerminalHistoryAsync(CancellationToken cancellationToken)
        => GetPendingAsync(cancellationToken);

    async Task<StoredLocalStateSnapshot> GetLocalStateAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var accepted = await GetLocalJobsAsync(cancellationToken).ConfigureAwait(false);
        var terminals = await GetTerminalHistoryAsync(cancellationToken).ConfigureAwait(false);
        return new StoredLocalStateSnapshot(
            accepted.Where(item => string.Equals(item.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)).ToArray(),
            terminals.Where(item => item.Envelope.Scope?.ScopeKind == ConnectorScopeKind.DeviceLocal
                                    && string.Equals(item.Envelope.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                .ToArray());
    }

    Task<IReadOnlyList<StoredLocalJob>> GetLocalJobsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<StoredLocalJob>>([]);

    Task SaveLocalQueuedAsync(
        string deviceId,
        ConnectorJobEnvelope job,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    Task MarkLocalRunningAsync(string requestId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    Task SaveTerminalAsync(ConnectorJobStatusEnvelope envelope, bool published, CancellationToken cancellationToken);

    Task SaveTerminalAsync(
        ConnectorJobStatusEnvelope envelope,
        bool published,
        CancellationToken cancellationToken,
        string? remoteAuthority)
        => SaveTerminalAsync(envelope, published, cancellationToken);

    Task SaveTerminalAsync(
        ConnectorJobStatusEnvelope envelope,
        bool published,
        CancellationToken cancellationToken,
        string? remoteAuthority,
        ConnectorJobEnvelope? sourceJob)
        => SaveTerminalAsync(envelope, published, cancellationToken, remoteAuthority);

    Task MarkPublishedAsync(string requestId, CancellationToken cancellationToken);
}
