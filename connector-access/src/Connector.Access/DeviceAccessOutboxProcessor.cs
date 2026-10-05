namespace Connector.Access;

public sealed class DeviceAccessOutboxProcessor
{
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

    public async ValueTask<ProviderDispatchResult?> ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var stored = await _repository.TryClaimNextAsync(
            Guid.NewGuid().ToString("N"),
            now,
            now.Add(_options.ProviderDispatchLease),
            cancellationToken);
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
            const string error = "provider_not_registered";
            await _repository.FailAsync(stored, error, now, now.Add(_options.ProviderRetryBackoff), cancellationToken);
            return new ProviderDispatchResult(stored.CommandId, stored.ProviderName, false, error);
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

            await _repository.CompleteAsync(stored, now, cancellationToken);
            return new ProviderDispatchResult(stored.CommandId, stored.ProviderName, true, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var error = exception.GetType().Name;
            await _repository.FailAsync(stored, error, now, now.Add(_options.ProviderRetryBackoff), cancellationToken);
            return new ProviderDispatchResult(stored.CommandId, stored.ProviderName, false, error);
        }
    }
}
