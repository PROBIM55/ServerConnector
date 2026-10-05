namespace Platform.Connector.Core;

public interface IProviderAdapter
{
    CadProvider Provider { get; }

    Task<ProviderExecutionResult> ExecuteAsync(ConnectorJobEnvelope job, CancellationToken cancellationToken);
}

public interface IConnectorJobExecutor
{
    string ExecutorId { get; }

    Task<ConnectorExecutionResult> ExecuteAsync(
        ConnectorJobEnvelope job,
        Func<ConnectorExecutionProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken);
}

public interface IJobStatusPublisher
{
    Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken);
}
