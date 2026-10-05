namespace Platform.Connector.Core;

public sealed class TrackingControlPlaneClient(
    IConnectorControlPlaneClient inner,
    ConnectorRuntimeState runtimeState) : IConnectorControlPlaneClient
{
    public async Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await inner.CheckHealthAsync(options, cancellationToken);
            runtimeState.MarkHealthCheck(success: true);
        }
        catch (Exception ex)
        {
            runtimeState.MarkHealthCheck(success: false, error: ex.Message);
            throw;
        }
    }

    public async Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var bootstrap = await inner.BootstrapAsync(options, cancellationToken);
            runtimeState.MarkBootstrap(bootstrap.SessionId, success: true);
            return bootstrap;
        }
        catch (Exception ex)
        {
            runtimeState.MarkBootstrap(options.SessionId, success: false, error: ex.Message);
            throw;
        }
    }

    public async Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await inner.SendHeartbeatAsync(options, cancellationToken);
            runtimeState.MarkHeartbeat(success: true);
        }
        catch (Exception ex)
        {
            runtimeState.MarkHeartbeat(success: false, error: ex.Message);
            throw;
        }
    }

    public async Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var job = await inner.TryPollJobAsync(options, cancellationToken);
            runtimeState.MarkPollResult(job);
            return job;
        }
        catch (Exception ex)
        {
            runtimeState.MarkPollResult(job: null, error: ex.Message);
            throw;
        }
    }

    public Task SendJobStatusAsync(ConnectorRuntimeOptions options, ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
    {
        return inner.SendJobStatusAsync(options, status, cancellationToken);
    }
}

public sealed class TrackingJobStatusPublisher(
    IJobStatusPublisher inner,
    ConnectorRuntimeState runtimeState) : IJobStatusPublisher
{
    public async Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
    {
        try
        {
            await inner.PublishAsync(status, cancellationToken);
            runtimeState.MarkStatus(status);
        }
        catch (Exception ex)
        {
            runtimeState.MarkStatusPublishError(ex.Message);
            throw;
        }
    }
}
