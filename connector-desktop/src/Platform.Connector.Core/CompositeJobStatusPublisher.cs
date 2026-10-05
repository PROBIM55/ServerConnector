namespace Platform.Connector.Core;

public sealed class CompositeJobStatusPublisher(params IJobStatusPublisher[] publishers) : IJobStatusPublisher
{
    public async Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
    {
        foreach (var publisher in publishers)
        {
            await publisher.PublishAsync(status, cancellationToken);
        }
    }
}
