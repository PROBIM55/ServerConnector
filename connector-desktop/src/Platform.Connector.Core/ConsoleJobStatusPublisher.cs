using System.Text.Json;

namespace Platform.Connector.Core;

public sealed class ConsoleJobStatusPublisher : IJobStatusPublisher
{
    public Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
    {
        var text = JsonSerializer.Serialize(status);
        Console.WriteLine(text);
        return Task.CompletedTask;
    }
}
