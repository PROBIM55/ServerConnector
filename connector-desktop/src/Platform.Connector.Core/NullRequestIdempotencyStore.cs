namespace Platform.Connector.Core;

public sealed class NullRequestIdempotencyStore : IRequestIdempotencyStore
{
    public static readonly NullRequestIdempotencyStore Instance = new();

    private NullRequestIdempotencyStore()
    {
    }

    public Task<StoredTerminalStatus?> TryGetAsync(string requestId, CancellationToken cancellationToken)
    {
        return Task.FromResult<StoredTerminalStatus?>(null);
    }

    public Task SaveTerminalAsync(ConnectorJobStatusEnvelope envelope, bool published, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task MarkPublishedAsync(string requestId, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
