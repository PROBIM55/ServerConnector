using Connector.Access;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connector.Access.AspNetCore;

// Runs inside Platform's existing host. Durable leases and retry eligibility
// belong to the repository; this worker neither owns another queue nor a timer
// that can recreate desired grants from cached permissions.
//
// One lane per required provider: commands of one provider are dispatched one
// at a time (the provider's per-device state stays single-writer), while
// providers progress independently. Total concurrency is bounded by the number
// of configured providers. A lane wakes immediately when a command is enqueued,
// when its own scheduled retry becomes due, or after the idle safety poll.
internal sealed class ConnectorAccessProviderDispatchWorker(
    IServiceScopeFactory scopes,
    ConnectorAccessDispatchSignal signal,
    IOptions<DeviceAccessOptions> options,
    TimeProvider clock,
    ILogger<ConnectorAccessProviderDispatchWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RetryWakeSlack = TimeSpan.FromMilliseconds(20);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Lanes cover the registered providers as well as the required ones, so
        // already stored commands (a revoke above all) of a provider that was
        // removed from the required list are still drained.
        string[] registered;
        using (var scope = scopes.CreateScope())
        {
            registered = scope.ServiceProvider.GetServices<IDeviceAccessGrantProvider>()
                .Select(provider => provider.ProviderName)
                .ToArray();
        }

        var lanes = options.Value.RequiredAccessProviders
            .Concat(registered)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (lanes.Length == 0)
        {
            return Task.CompletedTask;
        }

        return Task.WhenAll(lanes.Select(lane => Task.Run(() => RunLaneAsync(lane, stoppingToken), CancellationToken.None)));
    }

    private async Task RunLaneAsync(string providerName, CancellationToken stoppingToken)
    {
        DateTimeOffset? earliestRetry = null;
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = options.Value;
            var batchSize = Math.Max(1, settings.ProviderDispatchBatchSize);
            var idlePoll = settings.ProviderDispatchIdlePoll > TimeSpan.Zero
                ? settings.ProviderDispatchIdlePoll
                : TimeSpan.FromSeconds(5);
            var drained = true;
            TimeSpan? failureDelay = null;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<DeviceAccessOutboxProcessor>();
                var processed = 0;
                // Bound each pass so shutdown and unrelated host work stay
                // responsive even with a large number of pending devices.
                for (; processed < batchSize; processed++)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    var result = await processor.ProcessNextForProviderAsync(providerName, stoppingToken);
                    if (result is null) break;
                    if (result.RetryAtUtc is { } retryAt && (earliestRetry is null || retryAt < earliestRetry))
                    {
                        earliestRetry = retryAt;
                    }
                }

                drained = processed < batchSize;
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                consecutiveFailures++;
                failureDelay = exception is System.Data.Common.DbException { SqlState: "40001" or "40P01" }
                    ? TimeSpan.FromMilliseconds(Random.Shared.Next(50, 250))
                    : TimeSpan.FromSeconds(Math.Min(idlePoll.TotalSeconds, Math.Pow(2, Math.Min(consecutiveFailures, 10)) / 4));
                // Do not log request bodies, credentials or exception messages
                // from external providers; only the safe failure category.
                logger.LogWarning(
                    "Connector access dispatch for {Provider} will retry ({FailureType}).",
                    providerName,
                    exception.GetType().Name);
            }

            if (!drained && failureDelay is null)
            {
                // A full batch means more claimable work is likely waiting.
                continue;
            }

            var wait = failureDelay ?? idlePoll;
            if (failureDelay is null && earliestRetry is { } retry)
            {
                var untilRetry = retry - clock.GetUtcNow() + RetryWakeSlack;
                if (untilRetry < wait)
                {
                    wait = untilRetry > TimeSpan.Zero ? untilRetry : TimeSpan.Zero;
                    // The next pass claims (or reschedules) that command.
                    earliestRetry = null;
                }
            }

            try { await signal.WaitAsync(providerName, wait, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
