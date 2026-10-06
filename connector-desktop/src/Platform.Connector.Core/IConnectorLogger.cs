namespace Platform.Connector.Core;

public interface IConnectorLogger
{
    void Info(string message, ConnectorJobEnvelope? job = null, string? requestId = null);
    void Warn(string message, ConnectorJobEnvelope? job = null, string? requestId = null, Exception? exception = null);
    void Error(string message, ConnectorJobEnvelope? job = null, string? requestId = null, Exception? exception = null);
}
