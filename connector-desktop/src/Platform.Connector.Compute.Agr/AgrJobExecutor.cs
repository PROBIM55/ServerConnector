using System.Text.Json;
using Platform.Connector.Core;

namespace Platform.Connector.Compute.Agr;

public sealed class AgrJobExecutor(
    AgrBlenderProcessRunner runner,
    AgrJobInputMaterializer? inputMaterializer = null) : IConnectorJobExecutor
{
    public string ExecutorId => AgrComputeOptions.ExecutorId;

    public async Task<ConnectorExecutionResult> ExecuteAsync(
        ConnectorJobEnvelope job,
        Func<ConnectorExecutionProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        if (!job.Payload.TryGetProperty("manifestStoragePath", out var pathElement)
            || pathElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(pathElement.GetString()))
        {
            return new ConnectorExecutionResult(
                false,
                "AGR job payload.manifestStoragePath is required.",
                "AGR_PAYLOAD_INVALID");
        }

        try
        {
            if (inputMaterializer is not null)
            {
                await inputMaterializer.MaterializeAsync(job.Payload, cancellationToken);
            }
            var invocation = await runner.BuildAsync(
                pathElement.GetString()!,
                async update =>
                {
                    if (progress is null) return;
                    var message = ReadString(update, "message") ?? "AGR processing.";
                    var completed = ReadInt(update, "completed");
                    var total = ReadInt(update, "total");
                    var percentage = total > 0
                        ? 5 + (int)Math.Round(Math.Clamp(completed / (double)total, 0d, 1d) * 90d)
                        : 5;
                    await progress(
                        new ConnectorExecutionProgress(percentage, message, update),
                        cancellationToken);
                },
                cancellationToken);

            var manifestPath = runner.ResolveJobFilePath(pathElement.GetString()!);
            var result = JsonSerializer.SerializeToElement(new
            {
                localJobDirectory = Path.GetDirectoryName(manifestPath),
                worker = invocation.Result,
            });

            return new ConnectorExecutionResult(
                true,
                "AGR publication completed locally.",
                Result: result);
        }
        catch (FileNotFoundException exception)
        {
            return new ConnectorExecutionResult(false, exception.Message, "AGR_INPUT_NOT_FOUND");
        }
        catch (HttpRequestException exception)
        {
            return new ConnectorExecutionResult(false, exception.Message, "AGR_INPUT_TRANSFER_FAILED");
        }
        catch (InvalidDataException exception)
        {
            return new ConnectorExecutionResult(false, exception.Message, "AGR_INPUT_INVALID");
        }
        catch (TimeoutException exception)
        {
            return new ConnectorExecutionResult(false, exception.Message, "AGR_TIMEOUT");
        }
        catch (InvalidOperationException exception)
        {
            return new ConnectorExecutionResult(false, exception.Message, "AGR_WORKER_FAILED");
        }
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;
}
