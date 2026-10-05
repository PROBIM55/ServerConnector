using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Platform.Cad.AutoCad.Adapter.Sessions;
using Platform.Connector.Core;

namespace Platform.Cad.AutoCad.Adapter;

public sealed class AutoCadProviderAdapter : IProviderAdapter
{
    private const string LegacyBuildCommand = "_BRIDGE_BUILD_LAST_V2";

    /// <summary>Дефолтный лимит session-операций (COM-вызовы быстрые; legacy batch — 600 c).</summary>
    private const int DefaultSessionTimeoutSeconds = 120;

    private readonly Func<IAutoCadSessionExecutor> _sessionExecutorFactory;

    public AutoCadProviderAdapter()
        : this(sessionExecutorFactory: null)
    {
    }

    /// <summary>
    /// Constructor для тестов: подмена session-исполнителя (COM в CI недоступен).
    /// Фабрика, а не инстанс, — чтобы создание адаптера на машине без Windows/AutoCAD
    /// не трогало COM/STA вообще (ленивая инициализация на первом session-job'е).
    /// </summary>
    public AutoCadProviderAdapter(Func<IAutoCadSessionExecutor>? sessionExecutorFactory)
    {
        _sessionExecutorFactory = sessionExecutorFactory
            ?? (() => OperatingSystem.IsWindows()
                ? AutoCadSessionService.Shared
                : throw new PlatformNotSupportedException("AutoCAD COM sessions are Windows-only."));
    }

    public CadProvider Provider => CadProvider.AutoCad;

    public async Task<ProviderExecutionResult> ExecuteAsync(ConnectorJobEnvelope job, CancellationToken cancellationToken)
    {
        // mode=session → подключение к запущенному AutoCAD через COM/ROT;
        // иначе — прежний batch-путь (acad.exe /b + file-based IPC) без изменений.
        if (AutoCadSessionPayloadParser.IsSessionMode(job.Payload))
        {
            return await ExecuteSessionAsync(job, cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteLegacyBatchAsync(job, cancellationToken).ConfigureAwait(false);
    }

    // ════════════════════════ Session path (Wave B) ════════════════════════

    private async Task<ProviderExecutionResult> ExecuteSessionAsync(ConnectorJobEnvelope job, CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        var diagnosticsEnabled = TryGetBoolean(job.Payload, "diagnostics", out var diagnostics) && diagnostics;

        if (!OperatingSystem.IsWindows())
        {
            return BuildSessionFailure(job, startedAtUtc, AutoCadSessionErrorCodes.NotSupportedOnPlatform,
                "AutoCAD COM sessions are supported on Windows only.", null, diagnosticsEnabled);
        }

        if (!AutoCadSessionPayloadParser.TryParse(job.Payload, out var request, out var parseError))
        {
            return BuildSessionFailure(job, startedAtUtc, AutoCadSessionErrorCodes.PayloadInvalid,
                parseError ?? "Invalid session payload.", null, diagnosticsEnabled);
        }

        var timeoutSeconds = ResolveTimeoutSeconds(job.Payload, DefaultSessionTimeoutSeconds);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        AutoCadSessionOperationResult outcome;
        try
        {
            var executor = _sessionExecutorFactory();
            outcome = await executor.ExecuteAsync(request!, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BuildSessionFailure(job, startedAtUtc, "AUTOCAD_SESSION_TIMEOUT",
                $"AutoCAD session operation timed out after {timeoutSeconds}s.", request, diagnosticsEnabled);
        }
        catch (Exception ex)
        {
            return BuildSessionFailure(job, startedAtUtc, AutoCadSessionErrorCodes.ComError,
                $"AutoCAD session operation failed: {ex.Message}", request, diagnosticsEnabled);
        }

        return BuildSessionResult(job, startedAtUtc, request!, outcome, diagnosticsEnabled);
    }

    private static ProviderExecutionResult BuildSessionResult(
        ConnectorJobEnvelope job,
        DateTime startedAtUtc,
        AutoCadSessionRequest request,
        AutoCadSessionOperationResult outcome,
        bool diagnosticsEnabled)
    {
        using var resultDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                provider = "autocad",
                requestId = job.RequestId,
                operation = job.Operation.ToString(),
                mode = "session",
                action = ActionName(request.Action),
                sessionKey = outcome.Session?.SessionKey,
                session = outcome.Session is null
                    ? null
                    : new
                    {
                        sessionKey = outcome.Session.SessionKey,
                        processId = outcome.Session.ProcessId,
                        version = outcome.Session.Version,
                        documentName = outcome.Session.DocumentName,
                        documentPath = outcome.Session.DocumentPath
                    },
                handles = outcome.CreatedHandles,
                erasedCount = outcome.ErasedCount,
                notFoundHandles = outcome.NotFoundHandles,
                ping = outcome.Ping is null
                    ? null
                    : new
                    {
                        version = outcome.Ping.Version,
                        documentName = outcome.Ping.DocumentName,
                        documentPath = outcome.Ping.DocumentPath,
                        documentsCount = outcome.Ping.DocumentsCount,
                        modelSpaceCount = outcome.Ping.ModelSpaceCount
                    },
                diagnostics = diagnosticsEnabled
                    ? new
                    {
                        startedAtUtc,
                        finishedAtUtc = DateTime.UtcNow,
                        durationMs = (DateTime.UtcNow - startedAtUtc).TotalMilliseconds
                    }
                    : null
            }));

        return new ProviderExecutionResult(
            IsSuccess: outcome.IsSuccess,
            Message: outcome.IsSuccess
                ? (outcome.Message ?? "AutoCAD session command executed.")
                : (outcome.Message ?? "AutoCAD session command failed."),
            ErrorCode: outcome.IsSuccess ? null : (outcome.ErrorCode ?? AutoCadSessionErrorCodes.ComError),
            Result: resultDocument.RootElement.Clone());
    }

    private static ProviderExecutionResult BuildSessionFailure(
        ConnectorJobEnvelope job,
        DateTime startedAtUtc,
        string errorCode,
        string message,
        AutoCadSessionRequest? request,
        bool diagnosticsEnabled)
    {
        var failure = new AutoCadSessionOperationResult(false, errorCode, message);
        return BuildSessionResult(
            job,
            startedAtUtc,
            request ?? new AutoCadSessionRequest(AutoCadSessionAction.Ping, null, [], []),
            failure,
            diagnosticsEnabled);
    }

    private static string ActionName(AutoCadSessionAction action) => action switch
    {
        AutoCadSessionAction.Ping => "ping",
        AutoCadSessionAction.DrawEntities => "drawEntities",
        AutoCadSessionAction.EraseByHandles => "eraseByHandles",
        AutoCadSessionAction.ZoomExtents => "zoomExtents",
        _ => action.ToString()
    };

    // ════════════════════════ Legacy batch path (unchanged) ════════════════════════

    private static async Task<ProviderExecutionResult> ExecuteLegacyBatchAsync(ConnectorJobEnvelope job, CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        var diagnosticsEnabled = TryGetBoolean(job.Payload, "diagnostics", out var diagnostics) && diagnostics;
        var workingDirectory = ResolveWorkingDirectory(job.Payload);
        Directory.CreateDirectory(workingDirectory);

        var usePluginTransport =
            !TryGetString(job.Payload, "scriptPath", out _) &&
            TryGetString(job.Payload, "bridgePluginDllPath", out _);

        var pointerPath = TryGetString(job.Payload, "jobPointerPath", out var pointerOverride)
            ? pointerOverride
            : (usePluginTransport ? ResolvePluginPointerPath() : Path.Combine(workingDirectory, "job-pointer.json"));

        var statusPath = TryGetString(job.Payload, "buildStatusPath", out var statusOverride)
            ? statusOverride
            : (usePluginTransport ? ResolvePluginStatusPath() : Path.Combine(workingDirectory, "build-status.json"));

        var jobPath = TryGetString(job.Payload, "jobPath", out var resolvedJobPath) ? resolvedJobPath : null;
        if (!string.IsNullOrWhiteSpace(jobPath))
        {
            WriteJobPointer(pointerPath, job.RequestId, jobPath!);
            EnsurePendingBuildStatus(statusPath, job.RequestId, jobPath!);
        }

        var timeoutSeconds = ResolveTimeoutSeconds(job.Payload);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        if (!TryGetString(job.Payload, "autoCadExePath", out var exePath))
        {
            return BuildFailure(job, startedAtUtc, "AUTOCAD_EXE_REQUIRED", "payload.autoCadExePath is required.", diagnosticsEnabled, workingDirectory, pointerPath, statusPath);
        }

        if (!File.Exists(exePath))
        {
            return BuildFailure(job, startedAtUtc, "AUTOCAD_EXE_NOT_FOUND", $"AutoCAD executable is not found: {exePath}", diagnosticsEnabled, workingDirectory, pointerPath, statusPath);
        }

        var scriptPath = ResolveScriptPath(job.Payload, workingDirectory);
        if (!File.Exists(scriptPath))
        {
            var pluginPath = TryGetString(job.Payload, "bridgePluginDllPath", out var pluginDllPath) ? pluginDllPath : null;
            if (string.IsNullOrWhiteSpace(pluginPath) || !File.Exists(pluginPath))
            {
                return BuildFailure(
                    job,
                    startedAtUtc,
                    "PLUGIN_NOT_FOUND",
                    "Missing scriptPath and bridgePluginDllPath; cannot prepare AutoCAD run script.",
                    diagnosticsEnabled,
                    workingDirectory,
                    pointerPath,
                    statusPath);
            }

            var commandName = TryGetString(job.Payload, "commandName", out var command) ? command : LegacyBuildCommand;
            scriptPath = GenerateRunScript(workingDirectory, pluginPath!, commandName);
        }

        var arguments = $"/b \"{scriptPath}\"";
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return BuildFailure(
                job,
                startedAtUtc,
                "CAD_COMMAND_START_FAILED",
                $"Failed to start AutoCAD process: {ex.Message}",
                diagnosticsEnabled,
                workingDirectory,
                pointerPath,
                statusPath);
        }

        JsonElement? buildStatus = null;
        try
        {
            buildStatus = await WaitForBuildStatusOrExitAsync(process, statusPath, linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return BuildFailure(
                job,
                startedAtUtc,
                "BUILD_STATUS_TIMEOUT",
                $"AutoCAD execution timed out after {timeoutSeconds}s.",
                diagnosticsEnabled,
                workingDirectory,
                pointerPath,
                statusPath);
        }

        if (buildStatus is null && process.ExitCode != 0)
        {
            return BuildFailure(
                job,
                startedAtUtc,
                "CAD_COMMAND_FAILED",
                $"AutoCAD process exited with code {process.ExitCode}.",
                diagnosticsEnabled,
                workingDirectory,
                pointerPath,
                statusPath,
                process.ExitCode);
        }

        if (buildStatus is JsonElement statusJson &&
            statusJson.ValueKind == JsonValueKind.Object &&
            statusJson.TryGetProperty("status", out var stateProperty) &&
            stateProperty.ValueKind == JsonValueKind.String)
        {
            var state = stateProperty.GetString() ?? string.Empty;
            if (state.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("timeout", StringComparison.OrdinalIgnoreCase))
            {
                var statusMessage = statusJson.TryGetProperty("message", out var messageProperty) && messageProperty.ValueKind == JsonValueKind.String
                    ? messageProperty.GetString()
                    : "AutoCAD plugin reported an error status.";

                return BuildFailure(
                    job,
                    startedAtUtc,
                    state.Equals("timeout", StringComparison.OrdinalIgnoreCase) ? "BUILD_STATUS_TIMEOUT" : "COMMAND_DISPATCH_FAILED",
                    statusMessage ?? "AutoCAD plugin error.",
                    diagnosticsEnabled,
                    workingDirectory,
                    pointerPath,
                    statusPath,
                    process.ExitCode,
                    buildStatus);
            }

            if (state.Equals("success", StringComparison.OrdinalIgnoreCase))
            {
                return BuildSuccess(job, startedAtUtc, diagnosticsEnabled, workingDirectory, pointerPath, statusPath, process.HasExited ? process.ExitCode : 0, buildStatus);
            }
        }

        return BuildSuccess(job, startedAtUtc, diagnosticsEnabled, workingDirectory, pointerPath, statusPath, process.HasExited ? process.ExitCode : 0, buildStatus);
    }

    private static bool TryGetString(JsonElement payload, string key, out string value)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(key, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetBoolean(JsonElement payload, string key, out bool value)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(key, out var property) &&
            (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False))
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static int ResolveTimeoutSeconds(JsonElement payload, int defaultSeconds = 600)
    {
        var timeout = defaultSeconds;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return timeout;
        }

        if (payload.TryGetProperty("jobTimeoutSeconds", out var timeoutProperty) &&
            timeoutProperty.ValueKind == JsonValueKind.Number &&
            timeoutProperty.TryGetInt32(out var fromPayload) &&
            fromPayload >= 1)
        {
            timeout = fromPayload;
        }

        return Math.Clamp(timeout, 1, 86_400);
    }

    private static string ResolveWorkingDirectory(JsonElement payload)
    {
        if (TryGetString(payload, "workingDirectory", out var workingDirectory))
        {
            return workingDirectory;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BridgeBuilder");
    }

    private static string ResolveScriptPath(JsonElement payload, string workingDirectory)
    {
        if (TryGetString(payload, "scriptPath", out var scriptPath))
        {
            return scriptPath;
        }

        return Path.Combine(workingDirectory, "run.scr");
    }

    private static string ResolvePluginPointerPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BridgeBuilder",
            "job-pointer.json");
    }

    private static string ResolvePluginStatusPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BridgeBuilder",
            "build-status.json");
    }

    private static string GenerateRunScript(string workingDirectory, string pluginPath, string commandName)
    {
        var scriptPath = Path.Combine(workingDirectory, "run.scr");
        var content = new StringBuilder()
            .AppendLine("FILEDIA")
            .AppendLine("0")
            .AppendLine("CMDDIA")
            .AppendLine("0")
            .AppendLine("SECURELOAD")
            .AppendLine("0")
            .AppendLine("_NETLOAD")
            .AppendLine($"\"{pluginPath}\"")
            .AppendLine(commandName)
            .ToString();
        File.WriteAllText(scriptPath, content, Encoding.UTF8);
        return scriptPath;
    }

    private static async Task<JsonElement?> WaitForBuildStatusOrExitAsync(Process process, string statusPath, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryReadBuildStatus(statusPath, out var status))
            {
                if (status.ValueKind == JsonValueKind.Object &&
                    status.TryGetProperty("status", out var stateProp) &&
                    stateProp.ValueKind == JsonValueKind.String)
                {
                    var state = stateProp.GetString() ?? string.Empty;
                    if (state.Equals("success", StringComparison.OrdinalIgnoreCase) ||
                        state.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                        state.Equals("timeout", StringComparison.OrdinalIgnoreCase))
                    {
                        return status;
                    }
                }
            }

            if (process.HasExited)
            {
                return TryReadBuildStatus(statusPath, out var finalStatus) ? finalStatus : null;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static void WriteJobPointer(string pointerPath, string requestId, string jobPath)
    {
        var pointerPayload = new
        {
            requestId,
            jobPath,
            requestedAtUtc = DateTime.UtcNow
        };

        var pointerJson = JsonSerializer.Serialize(pointerPayload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(pointerPath, pointerJson, Encoding.UTF8);
    }

    private static void EnsurePendingBuildStatus(string statusPath, string requestId, string jobPath)
    {
        if (File.Exists(statusPath))
        {
            return;
        }

        var pendingPayload = new
        {
            requestId,
            status = "pending",
            message = "Build request prepared by connector.",
            jobPath,
            writtenAtUtc = DateTime.UtcNow
        };
        var statusJson = JsonSerializer.Serialize(pendingPayload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(statusPath, statusJson, Encoding.UTF8);
    }

    private static bool TryReadBuildStatus(string statusPath, out JsonElement status)
    {
        status = default;
        if (!File.Exists(statusPath))
        {
            return false;
        }

        try
        {
            using var statusDoc = JsonDocument.Parse(File.ReadAllText(statusPath, Encoding.UTF8));
            status = statusDoc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort process termination.
        }
    }

    private static ProviderExecutionResult BuildSuccess(
        ConnectorJobEnvelope job,
        DateTime startedAtUtc,
        bool diagnosticsEnabled,
        string workingDirectory,
        string pointerPath,
        string statusPath,
        int exitCode,
        JsonElement? buildStatus)
    {
        using var resultDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                provider = "autocad",
                requestId = job.RequestId,
                operation = job.Operation.ToString(),
                mode = "runtime",
                exitCode,
                buildStatus = buildStatus,
                diagnostics = diagnosticsEnabled
                    ? new
                    {
                        startedAtUtc,
                        finishedAtUtc = DateTime.UtcNow,
                        durationMs = (DateTime.UtcNow - startedAtUtc).TotalMilliseconds,
                        workingDirectory,
                        pointerPath,
                        statusPath
                    }
                    : null
            }));

        return new ProviderExecutionResult(
            IsSuccess: true,
            Message: "AutoCAD runtime command executed.",
            Result: resultDocument.RootElement.Clone());
    }

    private static ProviderExecutionResult BuildFailure(
        ConnectorJobEnvelope job,
        DateTime startedAtUtc,
        string errorCode,
        string message,
        bool diagnosticsEnabled,
        string workingDirectory,
        string pointerPath,
        string statusPath,
        int? exitCode = null,
        JsonElement? buildStatus = null)
    {
        using var resultDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                provider = "autocad",
                requestId = job.RequestId,
                operation = job.Operation.ToString(),
                mode = "runtime",
                exitCode,
                buildStatus = buildStatus,
                diagnostics = diagnosticsEnabled
                    ? new
                    {
                        startedAtUtc,
                        finishedAtUtc = DateTime.UtcNow,
                        durationMs = (DateTime.UtcNow - startedAtUtc).TotalMilliseconds,
                        workingDirectory,
                        pointerPath,
                        statusPath
                    }
                    : null
            }));

        return new ProviderExecutionResult(
            IsSuccess: false,
            Message: message,
            ErrorCode: errorCode,
            Result: resultDocument.RootElement.Clone());
    }
}
