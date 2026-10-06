using System.Diagnostics;
using Platform.Connector.Compute.Agr;
using Platform.Connector.Core;

namespace Connector.Platform;

public enum PlatformOperationStatus
{
    Accepted,
    Rejected,
    Failed
}

public sealed record PlatformOperationResult(PlatformOperationStatus Status, string Code, string Message)
{
    public bool Accepted => Status == PlatformOperationStatus.Accepted;

    public static PlatformOperationResult Accept(string code, string message) =>
        new(PlatformOperationStatus.Accepted, code, message);

    public static PlatformOperationResult Reject(string code, string message) =>
        new(PlatformOperationStatus.Rejected, code, message);

    public static PlatformOperationResult Fail(string code, string message) =>
        new(PlatformOperationStatus.Failed, code, message);
}

public sealed record PlatformConnectionSnapshot(
    bool IsConnected,
    bool HasStoredCredential,
    bool IsBusy,
    bool RequiresVpn,
    string Endpoint,
    ConnectorRuntimePhase Phase,
    string? ErrorCode,
    string? Message);

public interface IPlatformConnectionApi
{
    PlatformConnectionSnapshot Connection { get; }
    Task<PlatformOperationResult> ConnectAsync(string? oneTimeToken = null, CancellationToken cancellationToken = default);
    Task<PlatformOperationResult> ReconnectAsync(CancellationToken cancellationToken = default);
    Task<PlatformOperationResult> DisconnectAsync(CancellationToken cancellationToken = default);
    PlatformOperationResult ImportLegacyCredential();
}

/// <summary>
/// The only Platform composition in Structura Desktop. It uses the supplied
/// shared host and never provisions VPN: the Root orchestrator establishes the
/// common transport before calling this API.
/// </summary>
public sealed class PlatformRuntimeFacade : IPlatformConnectionApi, IAsyncDisposable
{
    private static readonly string[] LegacyProcessNames =
    [
        "Platform.Connector.App",
        "Platform.Connector.Desktop.Ui"
    ];

    private readonly AgrJobInputMaterializer _agrInputMaterializer = AgrConnectorIntegration.CreateInputMaterializer();
    private readonly ConnectorRuntimeHost _runtimeHost;
    private readonly PlatformSettingsPersistence _settingsPersistence;
    private readonly Action<ConnectorLogLine> _logForwarder;
    private readonly SynchronizationContext? _eventContext;
    private DesktopConnectorSettings? _settings;
    private string? _pendingCredential;
    private string? _credentialPersistenceError;
    private int _lifecycleOperation;

    public PlatformRuntimeFacade(
        ConnectorRuntimeHost runtimeHost,
        AgrJobInputMaterializer? agrInputMaterializer = null,
        PlatformSettingsPersistence? settingsPersistence = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeHost);
        _eventContext = SynchronizationContext.Current;
        if (agrInputMaterializer is not null) _agrInputMaterializer = agrInputMaterializer;
        _runtimeHost = runtimeHost;
        _settingsPersistence = settingsPersistence ?? new PlatformSettingsPersistence();
        _logForwarder = line =>
        {
            Log?.Invoke("Platform: " + line.Message);
            PublishSnapshot();
        };
        _runtimeHost.LogEmitted += _logForwarder;
    }

    public event Action<ConnectorRuntimeSnapshot>? SnapshotChanged;

    public Action<string>? Log { get; set; }

    public string AutomaticServerUrl => PlatformConnectionPolicy.AutomaticServerUrl;

    public bool RuntimeEnabled => IsPlatformHostActive();

    public bool IsConnected => Connection.IsConnected;

    public bool HasStoredCredential => _settingsPersistence.HasStoredCredential;

    public ConnectorRuntimeSnapshot Snapshot
    {
        get
        {
            CommitPendingCredentialIfConnected();
            return _runtimeHost.RuntimeState.GetSnapshot();
        }
    }

    public PlatformConnectionSnapshot Connection
    {
        get
        {
            var snapshot = Snapshot;
            var configured = IsPlatformHostConfigured();
            var active = configured && _runtimeHost.IsRunning;
            var isBusy = Volatile.Read(ref _lifecycleOperation) != 0 ||
                         (active && snapshot.Phase is ConnectorRuntimePhase.Connecting or ConnectorRuntimePhase.Reconnecting);
            var errorCode = configured && snapshot.Phase == ConnectorRuntimePhase.TokenInvalid
                ? "PLATFORM_TOKEN_REJECTED"
                : _credentialPersistenceError is not null
                    ? "PLATFORM_CREDENTIAL_SAVE_FAILED"
                : !active && _runtimeHost.IsRunning
                    ? "PLATFORM_SHARED_RUNTIME_BUSY"
                    : null;
            var message = errorCode switch
            {
                "PLATFORM_TOKEN_REJECTED" => "Сервер Platform отклонил сохранённый токен.",
                "PLATFORM_CREDENTIAL_SAVE_FAILED" => _credentialPersistenceError,
                "PLATFORM_SHARED_RUNTIME_BUSY" => "Общий runtime уже используется другим подключением.",
                _ => snapshot.LastError is null
                    ? null
                    : "Platform сообщил об ошибке подключения. Подробности доступны в локальном журнале."
            };
            return new(
                active && snapshot.Phase == ConnectorRuntimePhase.Connected && snapshot.BootstrapSuccessful,
                _settingsPersistence.HasStoredCredential,
                isBusy,
                PlatformConnectionPolicy.RequiresVpn,
                PlatformConnectionPolicy.AutomaticServerUrl,
                configured ? snapshot.Phase : ConnectorRuntimePhase.Stopped,
                errorCode,
                message);
        }
    }

    public string? GetRestartBlockReason()
    {
        if (Volatile.Read(ref _lifecycleOperation) != 0)
        {
            return "Подключение Platform запускается или останавливается.";
        }

        if (IsPlatformHostActive() && _runtimeHost.DrainSnapshot.Phase != ConnectorDrainPhase.ReadyToApply)
        {
            return "Platform подключён; обновление отложено до отключения.";
        }

        var legacyProcess = FindLegacyConnectorProcess();
        return legacyProcess is null
            ? null
            : $"Запущено прежнее приложение Platform Connector ({legacyProcess}.exe); обновление заблокировано.";
    }

    public PlatformOperationResult ImportLegacyCredential()
    {
        if (Volatile.Read(ref _lifecycleOperation) != 0 || _runtimeHost.IsRunning)
        {
            return PlatformOperationResult.Reject(
                "PLATFORM_RUNTIME_ACTIVE",
                "Отключите runtime перед импортом прежних настроек Platform.");
        }

        try
        {
            var imported = _settingsPersistence.ImportLegacy();
            if (!imported.Imported)
            {
                return PlatformOperationResult.Reject(imported.Code, imported.Message);
            }

            _settings = _settingsPersistence.Load();
            Log?.Invoke("Platform: прежние настройки перенесены в native DPAPI-хранилище; токен не раскрывался.");
            return PlatformOperationResult.Accept(imported.Code, imported.Message);
        }
        catch (Exception ex)
        {
            WriteLog($"Импорт прежних настроек не выполнен ({ex.GetType().Name}).");
            return PlatformOperationResult.Fail("PLATFORM_LEGACY_IMPORT_FAILED", "Не удалось импортировать прежние настройки Platform.");
        }
    }

    public async Task<PlatformOperationResult> ConnectAsync(
        string? oneTimeToken = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _lifecycleOperation, 1) != 0)
        {
            return PlatformOperationResult.Reject("PLATFORM_OPERATION_IN_PROGRESS", "Platform уже меняет состояние подключения.");
        }

        try
        {
            var legacyProcess = FindLegacyConnectorProcess();
            if (legacyProcess is not null)
            {
                return PlatformOperationResult.Reject(
                    "PLATFORM_LEGACY_RUNTIME_ACTIVE",
                    $"Закройте прежнее приложение Platform Connector ({legacyProcess}.exe)." );
            }

            if (_runtimeHost.IsRunning)
            {
                return IsPlatformHostActive()
                    ? PlatformOperationResult.Reject("PLATFORM_ALREADY_RUNNING", "Platform уже подключается или подключён.")
                    : PlatformOperationResult.Reject("PLATFORM_SHARED_RUNTIME_BUSY", "Общий runtime уже используется другим подключением.");
            }

            var token = oneTimeToken?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token) && !_settingsPersistence.TryLoadCredential(out token))
            {
                return PlatformOperationResult.Reject(
                    "PLATFORM_CREDENTIAL_REQUIRED",
                    "Введите токен Platform или импортируйте прежние настройки.");
            }

            _settings = _settingsPersistence.Load();
            _settings.ServerUrl = PlatformConnectionPolicy.AutomaticServerUrl;
            var options = CreateOptions(_settings, token);
            await AgrConnectorIntegration.ApplyRuntimeCapabilityAsync(options, WriteLog, cancellationToken);
            await _runtimeHost.StartAsync(options, WriteLog);
            _agrInputMaterializer.Configure(_runtimeHost.CurrentOptions ?? options);
            _credentialPersistenceError = null;
            _pendingCredential = token;
            PublishSnapshot();
            return PlatformOperationResult.Accept("PLATFORM_CONNECT_STARTED", "Подключение к Platform запущено.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlatformOperationResult.Reject("PLATFORM_CONNECT_CANCELLED", "Подключение к Platform отменено.");
        }
        catch (Exception ex)
        {
            WriteLog($"Подключение не запущено ({ex.GetType().Name}).");
            return PlatformOperationResult.Fail("PLATFORM_CONNECT_FAILED", "Не удалось запустить подключение к Platform.");
        }
        finally
        {
            Interlocked.Exchange(ref _lifecycleOperation, 0);
            PublishSnapshot();
        }
    }

    public async Task<PlatformOperationResult> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_runtimeHost.IsRunning)
        {
            return await ConnectAsync(cancellationToken: cancellationToken);
        }
        if (!IsPlatformHostActive())
        {
            return PlatformOperationResult.Reject("PLATFORM_SHARED_RUNTIME_BUSY", "Общий runtime уже используется другим подключением.");
        }
        if (Interlocked.Exchange(ref _lifecycleOperation, 1) != 0)
        {
            return PlatformOperationResult.Reject("PLATFORM_OPERATION_IN_PROGRESS", "Platform уже меняет состояние подключения.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _runtimeHost.RestartAsync(WriteLog);
            PublishSnapshot();
            return PlatformOperationResult.Accept("PLATFORM_RECONNECT_STARTED", "Переподключение к Platform запущено.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlatformOperationResult.Reject("PLATFORM_RECONNECT_CANCELLED", "Переподключение к Platform отменено.");
        }
        catch (Exception ex)
        {
            WriteLog($"Переподключение не запущено ({ex.GetType().Name}).");
            return PlatformOperationResult.Fail("PLATFORM_RECONNECT_FAILED", "Не удалось переподключить Platform.");
        }
        finally
        {
            Interlocked.Exchange(ref _lifecycleOperation, 0);
            PublishSnapshot();
        }
    }

    public async Task<PlatformOperationResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_runtimeHost.IsRunning)
        {
            return PlatformOperationResult.Reject("PLATFORM_ALREADY_STOPPED", "Platform уже отключён.");
        }
        if (!IsPlatformHostActive())
        {
            return PlatformOperationResult.Reject("PLATFORM_SHARED_RUNTIME_BUSY", "Нельзя остановить runtime другого подключения.");
        }
        if (Interlocked.Exchange(ref _lifecycleOperation, 1) != 0)
        {
            return PlatformOperationResult.Reject("PLATFORM_OPERATION_IN_PROGRESS", "Platform уже меняет состояние подключения.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _runtimeHost.StopAsync();
            _pendingCredential = null;
            _credentialPersistenceError = null;
            WriteLog("Подключение Platform отключено пользователем.");
            return PlatformOperationResult.Accept("PLATFORM_DISCONNECTED", "Platform отключён.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlatformOperationResult.Reject("PLATFORM_DISCONNECT_CANCELLED", "Отключение Platform отменено.");
        }
        catch (Exception ex)
        {
            WriteLog($"Отключение не выполнено ({ex.GetType().Name}).");
            return PlatformOperationResult.Fail("PLATFORM_DISCONNECT_FAILED", "Не удалось отключить Platform.");
        }
        finally
        {
            Interlocked.Exchange(ref _lifecycleOperation, 0);
            PublishSnapshot();
        }
    }

    public ValueTask DisposeAsync()
    {
        _runtimeHost.LogEmitted -= _logForwarder;
        _pendingCredential = null;
        return ValueTask.CompletedTask;
    }

    private static ConnectorRuntimeOptions CreateOptions(DesktopConnectorSettings settings, string token) => new()
    {
        ServerUrl = PlatformConnectionPolicy.AutomaticServerUrl,
        DeviceId = settings.DeviceId,
        DeviceToken = token,
        SecureTokenPath = settings.SecureTokenPath,
        HeartbeatSeconds = settings.HeartbeatSeconds,
        PollIntervalSeconds = settings.PollIntervalSeconds,
        PollBackoffMaxSeconds = settings.PollBackoffMaxSeconds,
        EnableJobPolling = true,
        AgentType = settings.AgentType,
        ModuleScope = settings.ModuleScope,
        Capabilities = [.. settings.Capabilities],
        UpdateManifestUrl = settings.UpdateManifestUrl
    };

    private bool IsPlatformHostActive()
    {
        return _runtimeHost.IsRunning && IsPlatformHostConfigured();
    }

    private bool IsPlatformHostConfigured() =>
        _runtimeHost.CurrentOptions is not null &&
        PlatformConnectionPolicy.IsAutomaticServerUrl(_runtimeHost.CurrentOptions.ServerUrl);

    private void CommitPendingCredentialIfConnected()
    {
        var pendingCredential = _pendingCredential;
        if (string.IsNullOrWhiteSpace(pendingCredential) || _settings is null || !IsPlatformHostConfigured()) return;
        var snapshot = _runtimeHost.RuntimeState.GetSnapshot();
        if (snapshot.Phase == ConnectorRuntimePhase.TokenInvalid)
        {
            _pendingCredential = null;
            return;
        }
        if (snapshot.Phase != ConnectorRuntimePhase.Connected || !snapshot.BootstrapSuccessful) return;

        _settings.SecureTokenPath = _runtimeHost.CurrentOptions?.SecureTokenPath ?? _settings.SecureTokenPath;
        try
        {
            _settingsPersistence.Save(_settings, pendingCredential);
            _credentialPersistenceError = null;
        }
        catch (Exception ex)
        {
            _credentialPersistenceError = "Platform подключён, но токен не удалось сохранить в DPAPI-хранилище.";
            WriteLog($"Токен не сохранён ({ex.GetType().Name}).");
        }
        finally
        {
            _pendingCredential = null;
        }
    }

    private static string? FindLegacyConnectorProcess()
    {
        foreach (var name in LegacyProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0) return name;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }

        return null;
    }

    private void PublishSnapshot()
    {
        var snapshot = _runtimeHost.RuntimeState.GetSnapshot();
        if (_eventContext is not null && SynchronizationContext.Current != _eventContext)
        {
            _eventContext.Post(_ => SnapshotChanged?.Invoke(snapshot), null);
            return;
        }

        SnapshotChanged?.Invoke(snapshot);
    }

    private void WriteLog(string message) => Log?.Invoke("Platform: " + message);
}
