using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Platform.Connector.Compute.Agr;

public sealed class AgrBlenderProcessRunner
{
    private const string ResultPrefix = "AGR_RESULT ";
    private const string ProgressPrefix = "AGR_PROGRESS ";
    private readonly AgrComputeOptions _options;
    private readonly string _jobStoreRoot;
    private readonly string _workerScriptPath;
    private readonly SemaphoreSlim _buildSlots;

    public AgrBlenderProcessRunner(AgrComputeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _jobStoreRoot = Path.GetFullPath(_options.JobStorePath);
        _workerScriptPath = Path.GetFullPath(_options.WorkerScriptPath);
        var concurrency = Math.Clamp(_options.MaxConcurrentBuilds, 1, 4);
        _buildSlots = new SemaphoreSlim(concurrency, concurrency);
    }

    public async Task<AgrRuntimeProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new(false, false, null, null, null, "AGR local compute runtime is disabled.");
        }

        var blenderPath = ResolveBlenderExecutable();
        if (Path.IsPathRooted(blenderPath) && !File.Exists(blenderPath))
        {
            return new(true, false, blenderPath, null, null, "Configured Blender executable was not found.");
        }
        if (!File.Exists(_workerScriptPath))
        {
            return new(true, false, blenderPath, null, null, "AGR worker script was not found.");
        }

        try
        {
            var invocation = await InvokeAsync(
                blenderPath,
                workingDirectory: Path.GetDirectoryName(_workerScriptPath) ?? AppContext.BaseDirectory,
                arguments: ["--preflight"],
                timeout: TimeSpan.FromSeconds(Math.Clamp(_options.PreflightTimeoutSeconds, 5, 120)),
                progress: null,
                cancellationToken);
            var blenderVersion = ReadString(invocation.Result, "blenderVersion");
            var workerVersion = ReadString(invocation.Result, "workerVersion");
            var ok = invocation.ExitCode == 0 && ReadBoolean(invocation.Result, "ok");
            return new(
                true,
                ok,
                blenderPath,
                blenderVersion,
                workerVersion,
                ok ? "AGR Blender runtime is ready." : invocation.Diagnostic);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(true, false, blenderPath, null, null, exception.Message);
        }
    }

    public async Task<AgrWorkerInvocationResult> BuildAsync(
        string manifestStoragePath,
        Func<JsonElement, Task>? progress,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("AGR local compute runtime is disabled.");
        }

        var manifestPath = ResolveJobFilePath(manifestStoragePath);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("AGR publication manifest was not found.", manifestPath);
        }
        if (!File.Exists(_workerScriptPath))
        {
            throw new FileNotFoundException("AGR worker script was not found.", _workerScriptPath);
        }

        await _buildSlots.WaitAsync(cancellationToken);
        try
        {
            var manifestTimeout = ReadManifestTimeout(manifestPath);
            var configuredTimeout = TimeSpan.FromMinutes(
                Math.Clamp(_options.BuildTimeoutMinutes, 1, 1440));
            var invocation = await InvokeAsync(
                ResolveBlenderExecutable(),
                Path.GetDirectoryName(manifestPath)!,
                ["--manifest", manifestPath],
                manifestTimeout < configuredTimeout ? manifestTimeout : configuredTimeout,
                progress,
                cancellationToken);
            if (invocation.ExitCode != 0 || !ReadBoolean(invocation.Result, "ok"))
            {
                throw new InvalidOperationException(
                    $"AGR Blender worker failed with exit code {invocation.ExitCode}. {invocation.Diagnostic}".Trim());
            }
            return invocation;
        }
        finally
        {
            _buildSlots.Release();
        }
    }

    public string ResolveJobFilePath(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            throw new ArgumentException("AGR job storage path is required.", nameof(storagePath));
        }

        var root = Path.GetFullPath(_jobStoreRoot);
        var candidate = Path.GetFullPath(Path.Combine(
            root,
            storagePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("AGR publication path escapes the configured job store.");
        }
        return candidate;
    }

    private async Task<AgrWorkerInvocationResult> InvokeAsync(
        string blenderPath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Func<JsonElement, Task>? progress,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = blenderPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("--background");
        start.ArgumentList.Add("--factory-startup");
        start.ArgumentList.Add("--disable-autoexec");
        start.ArgumentList.Add("--python");
        start.ArgumentList.Add(_workerScriptPath);
        start.ArgumentList.Add("--");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["BLENDER_USER_CONFIG"] = Path.Combine(workingDirectory, ".blender-config");
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start the AGR Blender worker.");
        }
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Priority adjustment is best-effort and is not supported on every host.
        }

        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
        var diagnostics = new Queue<string>();
        JsonElement? terminalResult = null;

        try
        {
            while (await process.StandardOutput.ReadLineAsync(linked.Token) is { } line)
            {
                if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
                {
                    if (progress is not null && TryParsePayload(line, ProgressPrefix, out var payload))
                    {
                        await progress(payload);
                    }
                    continue;
                }
                if (line.StartsWith(ResultPrefix, StringComparison.Ordinal)
                    && TryParsePayload(line, ResultPrefix, out var result))
                {
                    terminalResult = result;
                    continue;
                }
                AddDiagnostic(diagnostics, line);
            }
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"AGR Blender worker exceeded the {timeout} timeout.");
        }

        var stderr = await stderrTask;
        foreach (var line in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            AddDiagnostic(diagnostics, line);
        }
        var diagnostic = string.Join(Environment.NewLine, diagnostics);
        return new(
            process.ExitCode,
            terminalResult ?? JsonSerializer.SerializeToElement(new { ok = false }),
            diagnostic);
    }

    private string ResolveBlenderExecutable()
    {
        var configured = _options.BlenderExecutablePath;
        return string.IsNullOrWhiteSpace(configured) ? "blender" : configured.Trim();
    }

    private static bool TryParsePayload(string line, string prefix, out JsonElement payload)
    {
        try
        {
            using var document = JsonDocument.Parse(line[prefix.Length..]);
            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            payload = default;
            return false;
        }
    }

    private static void AddDiagnostic(Queue<string> diagnostics, string line)
    {
        var normalized = line.Trim();
        if (normalized.Length == 0) return;
        if (normalized.Length > 1000) normalized = normalized[..1000];
        diagnostics.Enqueue(normalized);
        while (diagnostics.Count > 50) diagnostics.Dequeue();
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static TimeSpan ReadManifestTimeout(string manifestPath)
    {
        using var stream = File.OpenRead(manifestPath);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("resourceLimits", out var resourceLimits)
            || !resourceLimits.TryGetProperty("timeoutSeconds", out var timeoutElement)
            || !timeoutElement.TryGetInt32(out var timeoutSeconds))
        {
            throw new InvalidOperationException(
                "AGR publication manifest has no valid resourceLimits.timeoutSeconds.");
        }
        return TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 86400));
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may exit between the inspection and termination.
        }
    }
}

public sealed record AgrRuntimeProbe(
    bool Enabled,
    bool Ready,
    string? BlenderExecutablePath,
    string? BlenderVersion,
    string? WorkerVersion,
    string Message);

public sealed record AgrWorkerInvocationResult(
    int ExitCode,
    JsonElement Result,
    string Diagnostic);
