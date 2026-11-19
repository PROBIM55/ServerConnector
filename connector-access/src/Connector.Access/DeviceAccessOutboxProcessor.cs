namespace Connector.Access;

public sealed class DeviceAccessOutboxProcessor
{
    private static readonly TimeSpan CancelledReleaseTimeout = TimeSpan.FromSeconds(5);
    private readonly DbProviderOutboxRepository _repository;
    private readonly IReadOnlyDictionary<string, IDeviceAccessGrantProvider> _providers;
    private readonly DeviceAccessOptions _options;
    private readonly TimeProvider _timeProvider;

    internal DeviceAccessOutboxProcessor(
        DbProviderOutboxRepository repository,
        IEnumerable<IDeviceAccessGrantProvider> providers,
        Microsoft.Extensions.Options.IOptions<DeviceAccessOptions> options,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _providers = providers
            .GroupBy(provider => provider.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public ValueTask<ProviderDispatchResult?> ProcessOneAsync(CancellationToken cancellationToken = default) =>
        ProcessNextAsync(null, cancellationToken);

    // Claims only commands of one provider so a slow or failing provider never
    // delays another provider's commands.
    public ValueTask<ProviderDispatchResult?> ProcessNextForProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return ProcessNextAsync(providerName, cancellationToken);
    }

    private async ValueTask<ProviderDispatchResult?> ProcessNextAsync(
        string? providerName,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var stored = await _repository.TryClaimNextAsync(
            Guid.NewGuid().ToString("N"),
            now,
            now.Add(_options.ProviderDispatchLease),
            cancellationToken,
            providerName);
        if (stored is null)
        {
            return null;
        }

        return await DispatchClaimedAsync(stored, cancellationToken);
    }

    internal async ValueTask<ProviderDispatchResult> DispatchClaimedAsync(
        StoredProviderCommand stored,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var fence = await _repository.TryBeginDispatchAsync(
            stored,
            now,
            now.Add(_options.ProviderDispatchLease),
            cancellationToken);
        if (fence != DispatchFenceStatus.Ready)
        {
            var error = fence == DispatchFenceStatus.Superseded ? "superseded" : "dispatch_busy";
            return new ProviderDispatchResult(stored.CommandId, stored.ProviderName, false, error);
        }

        var command = new DeviceAccessProviderCommand(
            stored.CommandId,
            stored.ProviderName,
            stored.Kind,
            stored.DeviceId,
            stored.UserId,
            stored.CompanyId,
            stored.DesiredRevision,
            stored.Modules,
            stored.Resources);
        if (!_providers.TryGetValue(stored.ProviderName, out var provider))
        {
            return await FailAsync(stored, "provider_not_registered", null, cancellationToken);
        }

        try
        {
            DeviceAccessProviderReceipt receipt;
            if (stored.Kind == DeviceAccessProviderCommandKind.Apply)
            {
                receipt = await provider.ApplyAsync(command, cancellationToken);
            }
            else
            {
                receipt = await provider.RevokeAsync(command, cancellationToken);
            }

            if (receipt.CommandId != command.CommandId || receipt.AppliedRevision != command.DesiredRevision ||
                receipt.Kind != command.Kind)
            {
                throw new InvalidOperationException("Provider receipt does not match the fenced command.");
            }

            await _repository.CompleteAsync(stored, _timeProvider.GetUtcNow(), cancellationToken);
            return new ProviderDispatchResult(stored.CommandId, stored.ProviderName, true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown: hand the claim back immediately instead of leaving
            // it 'processing' for the whole lease. Providers are idempotent per
            // (device, revision, kind), so the next dispatcher may repeat it.
            await ReleaseCancelledAsync(stored);
            throw;
        }
        catch (Exception exception)
        {
            // An HttpClient timeout surfaces as OperationCanceledException while
            // the dispatch token is still live; it is an ordinary retryable failure.
            var error = exception is OperationCanceledException ? "provider_timeout" : exception.GetType().Name;
            return await FailAsync(stored, error, exception, cancellationToken);
        }
    }

    private async ValueTask<ProviderDispatchResult> FailAsync(
        StoredProviderCommand stored,
        string error,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        var failedAt = _timeProvider.GetUtcNow();
        var (retryAt, deadLetter) = ProviderRetryPolicy.Next(_options, stored, exception, failedAt);
        var durableError = deadLetter ? ProviderRetryPolicy.DeadLetterPrefix + error : error;
        // Waits ("not ready", "busy") are refunded so only real failures count
        // toward parking.
        await _repository.FailAsync(
            stored, durableError, failedAt, retryAt, cancellationToken, ProviderRetryPolicy.IsWait(exception));
        return new ProviderDispatchResult(stored.CommandId, stored.ProviderName, false, durableError)
        {
            RetryAtUtc = deadLetter ? null : retryAt,
            DeadLettered = deadLetter,
        };
    }

    private async ValueTask ReleaseCancelledAsync(StoredProviderCommand stored)
    {
        try
        {
            using var release = new CancellationTokenSource(CancelledReleaseTimeout);
            var now = _timeProvider.GetUtcNow();
            await _repository.FailAsync(stored, "dispatch_cancelled", now, now, release.Token, refundAttempt: true);
        }
        catch (Exception)
        {
            // The lease still expires on its own; shutdown must not be blocked.
        }
    }
}
