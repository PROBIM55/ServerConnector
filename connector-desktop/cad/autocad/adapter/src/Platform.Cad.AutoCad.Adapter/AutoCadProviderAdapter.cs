using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Platform.Cad.AutoCad.Adapter.Sessions;
using Platform.Connector.Core;

namespace Platform.Cad.AutoCad.Adapter;

public sealed class AutoCadProviderAdapter : IProviderAdapter
{
    private const string LegacyBuildCommand = "_BRIDGE_BUILD_LAST_V2";

    /// <summary>Путь из payload отклонён: exe, скрипт и плагин определяет только локальная установка (аудит ИБ А1).</summary>
    public const string PayloadPathRejectedErrorCode = "AUTOCAD_PAYLOAD_PATH_REJECTED";

    /// <summary>payload.commandName не прошёл строгий фильтр имени команды AutoCAD.</summary>
    public const string CommandNameRejectedErrorCode = "AUTOCAD_COMMAND_NAME_REJECTED";

    /// <summary>Локальный каталог состояния legacy batch содержит точку повторной обработки (junction/symlink).</summary>
    public const string LocalStateUnsafeErrorCode = "AUTOCAD_LOCAL_STATE_UNSAFE";

    /// <summary>Дефолтный лимит session-операций (COM-вызовы быстрые; legacy batch — 600 c).</summary>
    private const int DefaultSessionTimeoutSeconds = 120;

    // \A…\z, а не ^…$: '$' в .NET совпадает и перед завершающим '\n'.
    private static readonly Regex CommandNamePattern = new(
        @"\A[A-Za-z_][A-Za-z0-9_\-]{0,63}\z",
        RegexOptions.CultureInvariant);

    private readonly Func<IAutoCadSessionExecutor> _sessionExecutorFactory;
    private readonly Func<string?> _autoCadExeResolver;
    private readonly string _pluginDirectory;
    private readonly string _localStateRoot;

    /// <summary>
    /// Единственный каталог, из которого legacy batch загружает плагин AutoCAD:
    /// подкаталог установки коннектора (perMachine, Program Files — запись только у администратора;
    /// AutoCAD считает Program Files доверенным, поэтому SECURELOAD понижать не нужно).
    /// </summary>
    public static string DefaultPluginDirectory => Path.Combine(AppContext.BaseDirectory, "plugins", "autocad");

    /// <summary>
    /// Собственный локальный каталог legacy batch: здесь лежат job-pointer.json и build-status.json,
    /// которые читает плагин, и run.scr каждого запуска (runs\&lt;guid&gt;). Payload эти пути не задаёт.
    /// </summary>
    public static string DefaultLocalStateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BridgeBuilder");

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
        : this(sessionExecutorFactory, ResolveInstalledAutoCadExe, DefaultPluginDirectory, DefaultLocalStateRoot)
    {
    }

    /// <summary>
    /// Constructor для тестов legacy batch: локальный поиск acad.exe, каталог плагинов
    /// и локальный каталог состояния подменяемы.
    /// </summary>
    public AutoCadProviderAdapter(
        Func<IAutoCadSessionExecutor>? sessionExecutorFactory,
        Func<string?> autoCadExeResolver,
        string pluginDirectory,
        string localStateRoot)
    {
        _sessionExecutorFactory = sessionExecutorFactory
            ?? (() => OperatingSystem.IsWindows()
                ? AutoCadSessionService.Shared
                : throw new PlatformNotSupportedException("AutoCAD COM sessions are Windows-only."));
        _autoCadExeResolver = autoCadExeResolver;
        _pluginDirectory = pluginDirectory;
        _localStateRoot = localStateRoot;
    }

    public CadProvider Provider => CadProvider.AutoCad;

    public async Task<ProviderExecutionResult> ExecuteAsync(ConnectorJobEnvelope job, CancellationToken cancellationToken)
    {
        // mode=session → подключение к запущенному AutoCAD через COM/ROT;
        // иначе — batch-путь (acad.exe /b + file-based IPC) с локальным разрешением exe/плагина (аудит ИБ А1).
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

    // ════════════════════════ Legacy batch path ════════════════════════
    //
    // Аудит ИБ А1: payload задания не может выбрать исполняемый файл, скрипт, DLL или
    // файлы состояния. acad.exe — только из локальной установки AutoCAD (HKLM), плагин — только
    // из каталога установки коннектора, run.scr генерируется локально в собственном каталоге
    // состояния, job-pointer/build-status — фиксированные файлы там же, commandName — строгий фильтр.
    // Проверки payload выполняются до любой записи на диск; сообщения отказов не раскрывают
    // локальные пути.

    private async Task<ProviderExecutionResult> ExecuteLegacyBatchAsync(ConnectorJobEnvelope job, CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        var diagnosticsEnabled = TryGetBoolean(job.Payload, "diagnostics", out var diagnostics) && diagnostics;
        var runsDirectory = Path.Combine(_localStateRoot, "runs");
        var workingDirectory = Path.Combine(runsDirectory, Guid.NewGuid().ToString("N"));
        var pointerPath = Path.Combine(_localStateRoot, "job-pointer.json");
        var statusPath = Path.Combine(_localStateRoot, "build-status.json");

        var jobPath = TryGetString(job.Payload, "jobPath", out var resolvedJobPath) ? resolvedJobPath : null;

        if (TryGetString(job.Payload, "scriptPath", out _))
        {
            return BuildFailure(job, startedAtUtc, PayloadPathRejectedErrorCode,
                "payload.scriptPath is not accepted: the AutoCAD run script is generated locally by the connector.",
                diagnosticsEnabled, workingDirectory, pointerPath, statusPath);
        }

        foreach (var field in new[] { "workingDirectory", "jobPointerPath", "buildStatusPath" })
        {
            if (TryGetString(job.Payload, field, out _))
            {
                return BuildFailure(job, startedAtUtc, PayloadPathRejectedErrorCode,
                    $"payload.{field} is not accepted: connector state files live in the connector's own local folder.",
                    diagnosticsEnabled, workingDirectory, pointerPath, statusPath);
            }
        }

        if (jobPath is not null && !IsLocalAbsolutePath(jobPath))
        {
            return BuildFailure(job, startedAtUtc, PayloadPathRejectedErrorCode,
                "payload.jobPath must be a local absolute path (UNC, device and relative paths are rejected).",
                diagnosticsEnabled, workingDirectory, pointerPath, statusPath);
        }

        var requestedExePath = TryGetString(job.Payload, "autoCadExePath", out var exeOverride) ? exeOverride : null;
        if (requestedExePath is not null && !IsLocalAbsolutePath(requestedExePath))
        {
            return BuildFailure(job, startedAtUtc, PayloadPathRejectedErrorCode,
                "payload.autoCadExePath must be a local absolute path; the executable is resolved from the local AutoCAD installation.",
                diagnosticsEnabled, workingDirectory, pointerPath, statusPath);
        }

        if (!TryGetString(job.Payload, "bridgePluginDllPath", out var requestedPluginPath))
        {
            return BuildFailure(
                job,
                startedAtUtc,
                "PLUGIN_NOT_FOUND",
                "payload.bridgePluginDllPath is required: legacy batch mode runs only an AutoCAD plugin installed with the connector.",
                diagnosticsEnabled,
                workingDirectory,
                pointerPath,
                statusPath);
        }

        if (!TryResolveInstalledPluginPath(requestedPluginPath, out var pluginPath))
        {
            return BuildFailure(job, startedAtUtc, PayloadPathRejectedErrorCode,
                "payload.bridgePluginDllPath must be a .dll inside the connector's AutoCAD plugin directory.",
                diagnosticsEnabled, workingDirectory, pointerPath, statusPath);
        }

        var commandName = TryGetString(job.Payload, "commandName", out var command) ? command : LegacyBuildCommand;
        if (!CommandNamePattern.IsMatch(commandName))
        {
            return BuildFailure(job, startedAtUtc, CommandNameRejectedErrorCode,
                "payload.commandName must match [A-Za-z_][A-Za-z0-9_-]{0,63}.",
                diagnosticsEnabled, workingDirectory, pointerPath, statusPath);
        }

        var exePath = _autoCadExeResolver();
        if (string.IsNullOrWhiteSpace(exePath) || !IsLocalAbsolutePath(exePath) || !File.Exists(exePath))
        {
            return BuildFailure(job, startedAtUtc, "AUTOCAD_EXE_NOT_FOUND",
                "Local AutoCAD installation is not found.",
                diagnosticsEnabled, workingDirectory, pointerPath, statusPath);
        }

        if (requestedExePath is not null && !PathsEqual(requestedExePath, exePath))
        {
            return BuildFailure(job, startedAtUtc, PayloadPathRejectedErrorCode,
                "payload.autoCadExePath does not match the locally installed AutoCAD.",
                diagnosticsEnabled, workingDirectory, pointerPath, statusPath);
        }

        if (!File.Exists(pluginPath))
        {
            return BuildFailure(
                job,
                startedAtUtc,
                "PLUGIN_NOT_FOUND",
                "The requested AutoCAD plugin is not installed with the connector.",
                diagnosticsEnabled,
                workingDirectory,
                pointerPath,
                statusPath);
        }

        if (!TryPrepareLocalState(runsDirectory, workingDirectory, pointerPath, statusPath))
        {
            return BuildFailure(job, startedAtUtc, LocalStateUnsafeErrorCode,
                "The connector's local AutoCAD state folder is not a plain local directory.",
                diagnosticsEnabled, workingDirectory, pointerPath, statusPath);
        }

        if (!string.IsNullOrWhiteSpace(jobPath))
        {
            WriteJobPointer(pointerPath, job.RequestId, jobPath!);
            EnsurePendingBuildStatus(statusPath, job.RequestId, jobPath!);
        }

        var timeoutSeconds = ResolveTimeoutSeconds(job.Payload);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var scriptPath = GenerateRunScript(workingDirectory, pluginPath, commandName);

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

    /// <summary>
    /// Локальный абсолютный путь вида <c>X:\…</c> на не-сетевом диске. UNC (<c>\\host\share</c>),
    /// пути устройств (<c>\\?\</c>, <c>\\.\</c>), относительные и drive-relative (<c>C:foo</c>) пути,
    /// кавычки и управляющие символы отклоняются.
    /// </summary>
    internal static bool IsLocalAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(ch => ch < ' ' || ch == '"'))
        {
            return false;
        }

        if (path.Length < 3 ||
            !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' ||
            (path[2] != '\\' && path[2] != '/') ||
            !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType != DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd('\\', '/'),
                Path.GetFullPath(right).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool TryResolveInstalledPluginPath(string requestedPath, out string pluginPath)
    {
        pluginPath = string.Empty;
        if (!IsLocalAbsolutePath(requestedPath) ||
            !IsLocalAbsolutePath(_pluginDirectory) ||
            !string.Equals(Path.GetExtension(requestedPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(requestedPath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_pluginDirectory)) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        pluginPath = fullPath;
        return true;
    }

    /// <summary>acad.exe последней установленной версии AutoCAD по HKLM (запись — только у администратора).</summary>
    private static string? ResolveInstalledAutoCadExe()
        => OperatingSystem.IsWindows() ? ResolveInstalledAutoCadExeFromRegistry() : null;

    [SupportedOSPlatform("windows")]
    private static string? ResolveInstalledAutoCadExeFromRegistry()
    {
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var autoCadKey = localMachine.OpenSubKey(@"SOFTWARE\Autodesk\AutoCAD");
            if (autoCadKey is null)
            {
                return null;
            }

            foreach (var release in PreferCurrent(autoCadKey, OrderReleasesDescending(autoCadKey.GetSubKeyNames())))
            {
                using var releaseKey = autoCadKey.OpenSubKey(release);
                if (releaseKey is null)
                {
                    continue;
                }

                foreach (var product in PreferCurrent(releaseKey, releaseKey.GetSubKeyNames()))
                {
                    using var productKey = releaseKey.OpenSubKey(product);
                    if (productKey?.GetValue("AcadLocation") is not string location || string.IsNullOrWhiteSpace(location))
                    {
                        continue;
                    }

                    var exePath = Path.Combine(location.Trim(), "acad.exe");
                    if (IsLocalAbsolutePath(exePath) && File.Exists(exePath))
                    {
                        return exePath;
                    }
                }
            }
        }
        catch
        {
            // Нет доступа к реестру — AutoCAD считается не найденным.
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> PreferCurrent(RegistryKey key, IEnumerable<string> subKeys)
    {
        var current = key.GetValue("CurVer") as string;
        var ordered = subKeys.ToList();
        if (!string.IsNullOrWhiteSpace(current) && ordered.Remove(current))
        {
            ordered.Insert(0, current);
        }

        return ordered;
    }

    private static IEnumerable<string> OrderReleasesDescending(IEnumerable<string> releases)
        => releases.OrderByDescending(
            release => Version.TryParse(release.TrimStart('R', 'r'), out var version) ? version : new Version(0, 0));

    /// <summary>
    /// Создаёт каталог состояния по уровням и проверяет, что ни корень, ни runs, ни каталог запуска,
    /// ни существующие job-pointer/build-status не являются reparse point (junction/symlink),
    /// — иначе запись/чтение ушли бы за пределы собственного каталога.
    /// </summary>
    private bool TryPrepareLocalState(string runsDirectory, string workingDirectory, string pointerPath, string statusPath)
    {
        try
        {
            if (!IsLocalAbsolutePath(_localStateRoot))
            {
                return false;
            }

            foreach (var directory in new[] { _localStateRoot, runsDirectory, workingDirectory })
            {
                if (Directory.Exists(directory) || File.Exists(directory))
                {
                    if (IsReparsePoint(directory) || !Directory.Exists(directory))
                    {
                        return false;
                    }
                }
                else
                {
                    Directory.CreateDirectory(directory);
                }
            }

            foreach (var file in new[] { pointerPath, statusPath })
            {
                if ((File.Exists(file) || Directory.Exists(file)) && (IsReparsePoint(file) || Directory.Exists(file)))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string GenerateRunScript(string workingDirectory, string pluginPath, string commandName)
    {
        // SECURELOAD не понижается: плагин лежит в каталоге установки (Program Files),
        // который AutoCAD считает доверенным.
        var scriptPath = Path.Combine(workingDirectory, "run.scr");
        var content = new StringBuilder()
            .AppendLine("FILEDIA")
            .AppendLine("0")
            .AppendLine("CMDDIA")
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
