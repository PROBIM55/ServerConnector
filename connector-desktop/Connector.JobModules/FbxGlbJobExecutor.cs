using System.Text.Json;
using Connector.AgrConversion;
using Connector.AgrConversion.Service;
using Platform.Connector.Core;

namespace Connector.JobModules;

public sealed class FbxGlbJobExecutor(string gltfpackPath) : IConnectorJobExecutor
{
    public const string Id = "converter.fbx-glb";
    public string ExecutorId => Id;

    public async Task<ConnectorExecutionResult> ExecuteAsync(ConnectorJobEnvelope job, Func<ConnectorExecutionProgress, CancellationToken, Task>? progress, CancellationToken cancellationToken)
    {
        if (!ConverterJobPayload.TryRead(job.Payload, out var payload, out var error))
            return new(false, error, "CONVERTER_PAYLOAD_INVALID");
        if (!File.Exists(payload!.InputPath) && !Directory.Exists(payload.InputPath))
            return new(false, "The FBX/AGR input path does not exist.", "CONVERTER_INPUT_NOT_FOUND");

        var attemptId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(payload.OutputDirectory, $".fbx-attempt-{attemptId}");
        var published = false;
        try
        {
            Directory.CreateDirectory(staging);
            var converter = new AgrGltfpackPartConverter(gltfpackPath);
            var update = new SynchronousProgress<AgrConvertProgress>(p =>
            {
                if (progress is null) return;
                var fraction = p.Steps <= 0 ? 0 : Math.Clamp(p.Step / (double)p.Steps, 0, 1);
                progress(new ConnectorExecutionProgress((int)Math.Round(fraction * 95), p.Stage), cancellationToken)
                    .GetAwaiter().GetResult();
            });
            var result = await Task.Run(() => converter.Convert(payload.InputPath, staging, Path.Combine(staging, "work"), update, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(result.ZipPath) || new FileInfo(result.ZipPath).Length == 0)
                return new(false, "AGR converter did not produce a valid archive.", "CONVERTER_OUTPUT_INVALID");

            var outputPath = result.ZipPath;
            var manifestPath = Path.Combine(staging, Path.GetFileNameWithoutExtension(outputPath) + ".manifest.json");
            await File.WriteAllTextAsync(manifestPath, result.Manifest?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var publishedDirectory = Path.Combine(payload.OutputDirectory, $"fbx-result-{attemptId}");
            Directory.Move(staging, publishedDirectory);
            published = true;
            outputPath = Path.Combine(publishedDirectory, Path.GetFileName(outputPath));
            manifestPath = Path.Combine(publishedDirectory, Path.GetFileName(manifestPath));
            var response = JsonSerializer.SerializeToElement(new
            {
                outputPath, manifestPath, sourcePath = payload.InputPath, profile = payload.Profile,
                agrResult = new { result.PartName, result.ZipBytes, result.ExtentM, result.Quantized,
                    result.TrianglesRef, result.TrianglesRefDecoded, result.Levels, result.Warnings,
                    result.TimingsS, result.Seconds, result.Manifest, result.PartReportJson }
            });
            return new(true, "FBX conversion completed.", Result: response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return new(false, "FBX conversion could not be completed.", "CONVERTER_FBX_FAILED");
        }
        finally
        {
            if (!published && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
