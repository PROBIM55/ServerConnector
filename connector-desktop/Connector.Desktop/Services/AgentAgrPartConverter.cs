using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.AgrConversion;
using Connector.AgrConversion.Service;
using Connector.Desktop.Features.ConverterWorkspace;
using Connector.JobModules;
using Platform.Connector.Core;

namespace Connector.Desktop.Services;

/// <summary>Retains the batch UI/report contract; all engine work is admitted by the shared Agent.</summary>
public sealed class AgentAgrPartConverter(IConverterJobClient client) : IAgrPartConverter
{
    public AgrConvertResult Convert(string input, string outputDirectory, string workRoot,
        IProgress<AgrConvertProgress> progress, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        void OnStatus(ConnectorJobStatusEnvelope status)
        {
            if (status.RequestId == requestId && status.Status == JobStatus.Running)
                progress.Report(new AgrConvertProgress(status.Message ?? "Конвертация", status.Progress ?? 0, 100));
        }
        client.StatusChanged += OnStatus;
        ConnectorJobStatusEnvelope terminal;
        try
        {
            terminal = client.SubmitAsync(requestId, FbxGlbJobExecutor.Id, input, outputDirectory, "default", cancellationToken)
                .GetAwaiter().GetResult();
        }
        finally { client.StatusChanged -= OnStatus; }
        if (terminal.Status == JobStatus.Cancelled) throw new OperationCanceledException(cancellationToken);
        if (terminal.Status != JobStatus.Success || terminal.Result is not { } data)
            throw new InvalidOperationException(terminal.Message ?? terminal.ErrorCode ?? "Не удалось завершить конвертацию.");

        var metadata = data.GetProperty("agrResult");
        var result = new AgrConvertResult
        {
            PartName = metadata.GetProperty("PartName").GetString()!,
            ZipPath = data.GetProperty("outputPath").GetString()!,
            ZipBytes = metadata.GetProperty("ZipBytes").GetInt64(),
            ExtentM = metadata.GetProperty("ExtentM").GetDouble(),
            Quantized = metadata.GetProperty("Quantized").GetBoolean(),
            TrianglesRef = metadata.GetProperty("TrianglesRef").GetInt64(),
            TrianglesRefDecoded = metadata.GetProperty("TrianglesRefDecoded").GetInt64(),
            Seconds = metadata.GetProperty("Seconds").GetDouble(),
            PartReportJson = metadata.GetProperty("PartReportJson").GetString(),
            Manifest = JsonNode.Parse(metadata.GetProperty("Manifest").GetRawText()) as JsonObject
        };
        result.Levels.AddRange(metadata.GetProperty("Levels").Deserialize<List<AgrLevelResult>>() ?? []);
        result.Warnings.AddRange(metadata.GetProperty("Warnings").Deserialize<List<string>>() ?? []);
        foreach (var value in metadata.GetProperty("TimingsS").EnumerateObject())
            result.TimingsS[value.Name] = value.Value.GetDouble();
        return result;
    }
}
