namespace Platform.Connector.Core;

public interface IConnectorControlPlaneClient
{
    Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken);

    Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken);

    Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken);

    Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken);

    Task SendJobStatusAsync(ConnectorRuntimeOptions options, ConnectorJobStatusEnvelope status, CancellationToken cancellationToken);
}
