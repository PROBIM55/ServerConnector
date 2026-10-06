using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Platform.Connector.Core;

namespace Connector.JobModules;

public sealed class IfcOptimizeOptions
{
    public string? ExecutablePath { get; init; }
    public IReadOnlyList<string>? ExecutableArguments { get; init; }
    public string? PythonExecutablePath { get; init; }
    public string? PythonRootPath { get; init; }
    public int MaxProtocolLineChars { get; init; } = 64 * 1024;
    public int MaxStandardErrorChars { get; init; } = 16 * 1024;
}

public sealed class IfcOptimizeJobExecutor : IConnectorJobExecutor
{
    public const string Id = "converter.ifc-optimize";
    public const string AnalyzeId = "converter.ifc-analyze";
    private const string Protocol = "structura-ifc-optimizer/1";
    private readonly IfcOptimizeOptions _options;
    private readonly string _command;

    public IfcOptimizeJobExecutor(IfcOptimizeOptions options) : this(options, Id, "optimize") { }

    /// <summary>One protocol client, parameterized only by a worker command and registered executor id.</summary>
    public IfcOptimizeJobExecutor(IfcOptimizeOptions options, string executorId, string command)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(executorId)) throw new ArgumentException("An executor id is required.", nameof(executorId));
        if (command is not ("optimize" or "analyze")) throw new ArgumentOutOfRangeException(nameof(command));
        ExecutorId = executorId;
        _command = command;
    }

    public string ExecutorId { get; }

    public async Task<ConnectorExecutionResult> ExecuteAsync(ConnectorJobEnvelope job, Func<ConnectorExecutionProgress, CancellationToken, Task>? progress, CancellationToken cancellationToken)
    {
        if (!ConverterJobPayload.TryRead(job.Payload, out var payload, out var error)) return new(false, error, "CONVERTER_PAYLOAD_INVALID");
        if (!File.Exists(payload!.InputPath)) return new(false, "The IFC input path does not exist.", "CONVERTER_INPUT_NOT_FOUND");
        if (!payload.InputPath.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase)) return new(false, "inputPath must be an IFC file.", "CONVERTER_INPUT_INVALID");
        if (_command == "optimize" && payload.Profile is not ("exact" or "compact")) return new(false, "profile must be exact or compact.", "CONVERTER_PROFILE_INVALID");

        var unavailable = TryCreateStartInfo(out var startInfo);
        if (unavailable is not null) return new(false, unavailable, "IFC_WORKER_UNAVAILABLE");
        var attemptId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(payload.OutputDirectory, $".ifc-attempt-{attemptId}");
        if (!IsDirectChild(payload.OutputDirectory, staging)) return new(false, "The output directory is invalid.", "CONVERTER_OUTPUT_INVALID");
        Directory.CreateDirectory(staging);
        var outputName = Path.GetFileNameWithoutExtension(payload.InputPath) + ".optimized.ifc";
        var stagedOutput = Path.Combine(staging, outputName);
        var stagedReport = Path.Combine(staging, _command == "optimize" ? Path.GetFileNameWithoutExtension(outputName) + ".report.json" : "analysis.json");
        OwnedWorkerProcess? process = null;
        Task<string>? stderrTask = null;
        var published = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process = OwnedWorkerProcess.Start(startInfo!);
            using var cancel = cancellationToken.Register(static state => ((OwnedWorkerProcess)state!).RequestTermination(), process);
            stderrTask = ReadBoundedAsync(process.StandardError, _options.MaxStandardErrorChars, CancellationToken.None);
            var requestPayload = _command == "optimize"
                ? JsonSerializer.SerializeToElement(new { input_path = payload.InputPath, output_path = stagedOutput, report_path = stagedReport, job_dir = staging, profile = payload.Profile })
                : JsonSerializer.SerializeToElement(new { input_path = payload.InputPath, job_dir = staging });
            var request = JsonSerializer.Serialize(new { protocol = Protocol, request_id = job.RequestId, command = _command, payload = requestPayload });
            await process.StandardInput.WriteLineAsync(request);
            process.StandardInput.Close();

            JsonElement? terminal = null;
            await foreach (var line in ReadLinesBoundedAsync(process.StandardOutput, _options.MaxProtocolLineChars, cancellationToken))
            {
                using var document = JsonDocument.Parse(line);
                var eventData = document.RootElement.Clone();
                if (!eventData.TryGetProperty("protocol", out var protocol) || protocol.GetString() != Protocol
                    || !eventData.TryGetProperty("request_id", out var requestId) || requestId.GetString() != job.RequestId)
                    throw new WorkerFailureException("IFC_PROTOCOL_INVALID", "IFC worker returned an incompatible protocol event.");
                var kind = eventData.TryGetProperty("event", out var kindValue) ? kindValue.GetString() : null;
                if (kind == "error") throw new WorkerFailureException("IFC_WORKER_FAILED", "IFC worker rejected the conversion request.");
                if ((_command == "optimize" && kind == "completed") || (_command == "analyze" && kind == "analysis")) terminal = eventData;
                if (progress is not null)
                {
                    var ratio = eventData.TryGetProperty("progress", out var raw) && raw.TryGetDouble(out var parsed) ? parsed : 0;
                    await progress(new ConnectorExecutionProgress((int)Math.Round(Math.Clamp(ratio, 0, 1) * 95), ReadMessage(eventData), eventData), cancellationToken);
                }
            }
            await process.WaitForExitAsync(cancellationToken);
            _ = await stderrTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new WorkerFailureException("IFC_WORKER_FAILED", "IFC worker failed to complete the conversion.");
            if (terminal is null) throw new WorkerFailureException("IFC_PROTOCOL_INCOMPLETE", "IFC worker exited without a completion event.");
            if ((_command == "optimize" && (!File.Exists(stagedOutput) || new FileInfo(stagedOutput).Length == 0)) || !File.Exists(stagedReport))
                throw new WorkerFailureException("IFC_OUTPUT_INVALID", "IFC worker completed without validated output files.");

            Directory.CreateDirectory(payload.OutputDirectory);
            var publishedDirectory = Path.Combine(payload.OutputDirectory, $"ifc-result-{attemptId}");
            await RebaseReportPathsAsync(stagedReport, staging, publishedDirectory, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, publishedDirectory);
            published = true;
            var outputPath = _command == "optimize" ? Path.Combine(publishedDirectory, outputName) : null;
            var reportPath = Path.Combine(publishedDirectory, Path.GetFileName(stagedReport));
            var result = JsonSerializer.SerializeToElement(new { outputPath, reportPath, sourcePath = payload.InputPath, profile = payload.Profile, operation = _command, workerEvent = terminal });
            if (progress is not null)
            {
                try
                {
                    await progress(new ConnectorExecutionProgress(100, _command == "optimize" ? "IFC optimization completed." : "IFC analysis completed.", terminal), CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    // Publication is the commit boundary; late progress cancellation cannot roll it back.
                }
            }
            return new(true, _command == "optimize" ? "IFC optimization completed." : "IFC analysis completed.", Result: result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkerFailureException failure)
        {
            return new(false, failure.Message, failure.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return new(false, "IFC conversion could not be completed.", "IFC_WORKER_FAILED");
        }
        finally
        {
            if (process is not null) await process.StopAsync();
            if (stderrTask is not null) _ = await stderrTask;
            process?.Dispose();
            if (!published && IsDirectChild(payload.OutputDirectory, staging) && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private string? TryCreateStartInfo(out ProcessStartInfo? info)
    {
        info = null;
        if (!string.IsNullOrWhiteSpace(_options.ExecutablePath))
        {
            var executable = Path.GetFullPath(_options.ExecutablePath);
            if (!File.Exists(executable)) return "Configured IFC worker executable is unavailable.";
            info = CreateProcessInfo(executable);
            foreach (var argument in _options.ExecutableArguments ?? ["serve"]) info.ArgumentList.Add(argument);
            return null;
        }
        if (string.IsNullOrWhiteSpace(_options.PythonExecutablePath) || string.IsNullOrWhiteSpace(_options.PythonRootPath))
            return "Configure either ExecutablePath or PythonExecutablePath with PythonRootPath for the IFC worker.";
        var python = Path.GetFullPath(_options.PythonExecutablePath);
        var root = Path.GetFullPath(_options.PythonRootPath);
        if (!File.Exists(python) || !Directory.Exists(Path.Combine(root, "src"))) return "Configured Python or IFC worker root is unavailable.";
        info = CreateProcessInfo(python); info.ArgumentList.Add("-m"); info.ArgumentList.Add("structura_ifc_optimizer"); info.ArgumentList.Add("serve"); info.Environment["PYTHONPATH"] = Path.Combine(root, "src"); return null;
    }

    private static ProcessStartInfo CreateProcessInfo(string executable) => new(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    private static string ReadMessage(JsonElement value) => value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString()! : "IFC worker progress.";
    private static bool IsDirectChild(string parent, string child)
    {
        var canonicalParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        var canonicalChild = Path.GetFullPath(child);
        return canonicalChild.StartsWith(canonicalParent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && string.Equals(Path.GetDirectoryName(canonicalChild), Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    private static async Task RebaseReportPathsAsync(string reportPath, string oldDirectory, string newDirectory, CancellationToken cancellationToken)
    {
        var report = await File.ReadAllTextAsync(reportPath, cancellationToken);
        await File.WriteAllTextAsync(reportPath, report.Replace(oldDirectory, newDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), cancellationToken);
    }
    private sealed class WorkerFailureException(string code, string message) : Exception(message) { public string Code { get; } = code; }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var buffer = new char[1024]; var text = new StringBuilder(); int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            if (text.Length < limit) text.Append(buffer, 0, Math.Min(read, limit - text.Length));
        return text.ToString();
    }

    private static async IAsyncEnumerable<string> ReadLinesBoundedAsync(StreamReader reader, int limit, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new char[1024]; var line = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken); if (read == 0) yield break;
            for (var index = 0; index < read; index++)
            {
                var current = buffer[index]; if (current == '\r') continue;
                if (current == '\n') { yield return line.ToString(); line.Clear(); continue; }
                if (line.Length >= limit) throw new InvalidDataException("IFC worker protocol line exceeded configured limit.");
                line.Append(current);
            }
        }
    }
}
