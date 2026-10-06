namespace Platform.Connector.Core;

public sealed class ServerJobStatusPublisher(
    IConnectorControlPlaneClient client,
    Func<ConnectorRuntimeOptions> optionsAccessor) : IJobStatusPublisher
{
    public Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
    {
        var options = optionsAccessor();
        return client.SendJobStatusAsync(options, status, cancellationToken);
    }
}
