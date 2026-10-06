using Platform.Connector.Core;

namespace Connector.Desktop.Features.ConverterWorkspace;

/// <summary>Desktop boundary to the common Connector agent; it never performs conversion itself.</summary>
public interface IConverterJobClient
{
    event Action<ConnectorJobStatusEnvelope>? StatusChanged;

    Task<ConnectorJobStatusEnvelope> SubmitAsync(ConverterJobRequest request, CancellationToken cancellationToken);

    // Kept while existing AGR callers move to the typed request boundary.
    Task<ConnectorJobStatusEnvelope> SubmitAsync(string requestId, string executorId, string inputPath, string outputDirectory, string profile, CancellationToken cancellationToken);
}

public enum ConverterJobOperation
{
    Optimize,
    Analyze
}

public sealed record ConverterJobRequest(
    string RequestId,
    string ExecutorId,
    string InputPath,
    string OutputDirectory,
    string Profile,
    ConverterJobOperation Operation,
    ConnectorProductId Product);
