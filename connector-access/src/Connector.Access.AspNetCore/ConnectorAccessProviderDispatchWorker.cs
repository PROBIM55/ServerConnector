using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Connector.Access.AspNetCore;

// Runs inside Platform's existing host. Durable leases and retry eligibility
// belong to the repository; this worker neither owns another queue nor a timer
// that can recreate desired grants from cached permissions.
internal sealed class ConnectorAccessProviderDispatchWorker(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<ConnectorAccessProviderDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<DeviceAccessOutboxProcessor>();
                // Bound each pass so shutdown and unrelated host work stay
                // responsive even with a large number of pending devices.
                for (var index = 0; index < 32; index++)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    if (await processor.ProcessOneAsync(stoppingToken) is null) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Do not log request bodies, credentials or exception messages
                // from external providers; only the safe failure category.
                logger.LogWarning("Connector access dispatch will retry ({FailureType}).", exception.GetType().Name);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
