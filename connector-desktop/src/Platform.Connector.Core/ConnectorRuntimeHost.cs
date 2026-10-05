using System.IO;
using System.Net.Http;

namespace Platform.Connector.Core;

/// <summary>
/// Хост runtime: собирает компоненты (control plane, runner, worker) и держит
/// их под <see cref="ConnectorRuntimeSupervisor"/>. Start/Stop/Restart безопасны
/// в любой момент: остановка — чистая (cancel + await, без осиротевших циклов),
/// повторный Start при живом runtime — no-op.
/// </summary>
public sealed class ConnectorRuntimeHost : IAsyncDisposable
{
    private readonly IReadOnlyList<IProviderAdapter> _providerAdapters;
    private readonly IReadOnlyList<IConnectorJobExecutor> _jobExecutors;
    private readonly bool _writeLogsToConsole;
    private readonly bool _includeConsolePublisher;
    private readonly string _runtimeRootDirectory;
    private readonly string _localDeviceId;
    private readonly HttpMessageHandler? _httpMessageHandler;
    private readonly object _executionSync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConnectorExecutionGate _executionGate = new();

    private ConnectorRuntimeState _runtimeState = new();
    private ConnectorJobDispatcher? _dispatcher;
    private ConnectorJobRunner? _runner;
    private IRequestIdempotencyStore? _idempotencyStore;
    private IJobStatusPublisher? _localStatusPublisher;
    private CancellationTokenSource? _cts;
    private Task? _supervisorTask;
    private HttpClient? _httpClient;
    private readonly Func<ConnectorRuntimeOptions, IConnectorControlPlaneClient>? _controlPlaneClientFactory;
    private JsonLineConnectorLogger? _logger;
    private FileStream? _executionLease;
    private int _disposed;

    public ConnectorRuntimeHost(
        IEnumerable<IProviderAdapter> providerAdapters,
        IEnumerable<IConnectorJobExecutor>? jobExecutors = null,
        bool writeLogsToConsole = false,
        bool includeConsolePublisher = false,
        string? runtimeRootDirectory = null,
        IEnumerable<SharedModuleDescriptor>? moduleDescriptors = null,
        string? localDeviceId = null,
        HttpMessageHandler? httpMessageHandler = null,
        Func<ConnectorRuntimeOptions, IConnectorControlPlaneClient>? controlPlaneClientFactory = null)
    {
        _providerAdapters = providerAdapters.ToArray();
        _jobExecutors = (jobExecutors ?? []).ToArray();
        _writeLogsToConsole = writeLogsToConsole;
        _includeConsolePublisher = includeConsolePublisher;
        _runtimeRootDirectory = string.IsNullOrWhiteSpace(runtimeRootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Platform", "Connector", "desktop")
            : runtimeRootDirectory;
        _localDeviceId = string.IsNullOrWhiteSpace(localDeviceId)
            ? $"local-{Environment.MachineName.ToLowerInvariant()}"
            : localDeviceId.Trim();
        _httpMessageHandler = httpMessageHandler;
        _controlPlaneClientFactory = controlPlaneClientFactory;
        ModuleCatalog = new SharedModuleCatalog(moduleDescriptors);
    }

    /// <summary>Каждая строка структурного лога runtime (для live-журнала UI).</summary>
    public event Action<ConnectorLogLine>? LogEmitted;

    /// <summary>Every local or remote job status emitted by the shared runner.</summary>
    public event Action<ConnectorJobStatusEnvelope>? JobStatusChanged;

    public bool IsRunning => _supervisorTask is { IsCompleted: false };

    public ConnectorRuntimeState RuntimeState => _runtimeState;

    public SharedModuleCatalog ModuleCatalog { get; }

    public ConnectorDrainSnapshot DrainSnapshot => _executionGate.Snapshot;

    /// <summary>Подготовка к обновлению без отмены уже принятой работы.</summary>
    public Task<ConnectorDrainSnapshot> RequestDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _executionGate.RequestDrainAsync(timeout, cancellationToken);

    /// <summary>Atomically closes admission and joins an in-progress drain.</summary>
    public IDisposable BeginProtectedDrain() => _executionGate.BeginProtectedDrain();

    /// <summary>Waits for an existing drain or starts a new one.</summary>
    public Task<ConnectorDrainSnapshot> RequestDrainOrJoinAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _executionGate.RequestDrainOrJoinAsync(timeout, cancellationToken);

    public void Resume() => _executionGate.Resume();

    public ConnectorRuntimeOptions? CurrentOptions { get; private set; }

    public string RuntimeRootDirectory => _runtimeRootDirectory;

    /// <summary>
    /// Reads an immutable history snapshot for this host's local identity without
    /// starting or replaying any accepted work. Call after UI status subscriptions.
    /// </summary>
    public async Task<LocalRecoverySnapshot> PrepareLocalAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureExecutionInitialized();
        var stored = await _idempotencyStore!
            .GetLocalStateAsync(_localDeviceId, cancellationToken)
            .ConfigureAwait(false);
        var jobs = new List<LocalJobSnapshot>(stored.AcceptedJobs.Count + stored.TerminalResults.Count);
        jobs.AddRange(stored.AcceptedJobs.Select(_runner!.BuildStoredLocalSnapshot));
        foreach (var terminal in stored.TerminalResults)
        {
            if (terminal.SourceJob is null)
            {
                _logger!.Warn(
                    "Stored device-local terminal has no immutable source envelope and is excluded from local history.",
                    requestId: terminal.Envelope.RequestId);
                continue;
            }
            jobs.Add(new LocalJobSnapshot(
                terminal.SourceJob,
                terminal.Envelope,
                PersistedState: null,
                terminal.SavedAtUtc));
        }

        var ordered = jobs
            .OrderBy(item => item.Job.CreatedAtUtc)
            .ThenBy(item => item.SavedAtUtc)
            .ThenBy(item => item.Job.RequestId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new LocalRecoverySnapshot(_localDeviceId, Array.AsReadOnly(ordered));
    }

    /// <summary>
    /// Recovers only work owned by this host's stable local identity. Queued work is
    /// returned to the shared FIFO; previously Running work becomes Interrupted.
    /// </summary>
    public async Task RecoverLocalAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureExecutionInitialized();
        await _dispatcher!.RecoverLocalAsync(_localDeviceId).ConfigureAwait(false);
    }

    /// <summary>Queues an offline-capable device-local job in the shared FIFO runtime.</summary>
    public async Task<ConnectorJobStatusEnvelope> SubmitLocalAsync(
        ConnectorJobEnvelope job,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureExecutionInitialized();
        await RecoverLocalAsync(cancellationToken).ConfigureAwait(false);
        return await _dispatcher!.SubmitLocalAsync(_localDeviceId, job, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Запускает supervisor и сразу возвращается: bootstrap и переподключения
    /// идут в фоне, прогресс виден через <see cref="RuntimeState"/> (Phase).
    /// Бросает только на пустой токен.
    /// </summary>
    public async Task StartAsync(ConnectorRuntimeOptions options, Action<string>? log = null)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if (IsRunning)
            {
                return;
            }

            if (!Enum.IsDefined(options.AuthenticationMode) ||
                (options.AuthenticationMode == ConnectorAuthenticationMode.IssuedCertificate && _controlPlaneClientFactory is null))
                throw new InvalidOperationException("Issued-certificate control plane is not configured.");
            if (options.AuthenticationMode == ConnectorAuthenticationMode.LegacyDeviceToken && string.IsNullOrWhiteSpace(options.DeviceToken))
            {
                throw new InvalidOperationException("Token is empty.");
            }

            CurrentOptions = CloneOptions(options);
            EnsureRuntimePaths(CurrentOptions);
            EnsureExecutionInitialized();
            await RecoverLocalAsync().ConfigureAwait(false);

            IConnectorControlPlaneClient rawControlPlane;
            if (CurrentOptions.AuthenticationMode == ConnectorAuthenticationMode.IssuedCertificate)
            {
                rawControlPlane = _controlPlaneClientFactory!(CurrentOptions) ??
                    throw new InvalidOperationException("Issued-certificate control plane is not configured.");
            }
            else
            {
                _httpClient = _httpMessageHandler is null
                    ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }
                    : new HttpClient(_httpMessageHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
                rawControlPlane = new HttpConnectorControlPlaneClient(_httpClient);
            }

            _cts = new CancellationTokenSource();
            _runtimeState = new ConnectorRuntimeState();
            _runtimeState.MarkPhase(ConnectorRuntimePhase.Connecting);

            var controlPlane = new TrackingControlPlaneClient(rawControlPlane, _runtimeState);

            foreach (var adapter in _providerAdapters)
            {
                _runtimeState.RegisterProvider(adapter.Provider);
            }

            var serverPublisher = new TrackingJobStatusPublisher(
                new ServerJobStatusPublisher(controlPlane, () => CurrentOptions ?? throw new InvalidOperationException("Runtime options are not initialized.")),
                _runtimeState);
            var statusPublisher = new CompositeJobStatusPublisher(_localStatusPublisher!, serverPublisher);
            var worker = new ConnectorWorker(
                controlPlane,
                _runner!,
                statusPublisher,
                _idempotencyStore!,
                _logger!,
                _executionGate,
                _dispatcher);
            var supervisor = new ConnectorRuntimeSupervisor(controlPlane, worker, _runtimeState, _logger!);

            var runOptions = CurrentOptions;
            var token = _cts.Token;
            _supervisorTask = Task.Run(() => supervisor.RunAsync(runOptions, token, log), CancellationToken.None);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_cts is null)
            {
                return;
            }

            _cts.Cancel();
            if (_supervisorTask is not null)
            {
                try
                {
                    await _supervisorTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }
            }

            _cts.Dispose();
            _cts = null;
            _supervisorTask = null;
            _httpClient?.Dispose();
            _httpClient = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Чистый перезапуск с текущими опциями (например, по кнопке «Переподключить»).</summary>
    public async Task RestartAsync(Action<string>? log = null)
    {
        var drain = _executionGate.Snapshot;
        if (drain.Phase != ConnectorDrainPhase.Running)
        {
            throw new InvalidOperationException(
                $"Connector cannot restart while drain phase is {drain.Phase}. Call Resume explicitly before reconnecting.");
        }

        var options = CurrentOptions
            ?? throw new InvalidOperationException("Runtime was never started — nothing to restart.");
        await StopAsync();
        await StartAsync(options, log);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync();
        ConnectorJobDispatcher? dispatcher;
        JsonLineConnectorLogger? logger;
        lock (_executionSync)
        {
            dispatcher = _dispatcher;
            logger = _logger;
        }
        if (dispatcher is not null)
        {
            await dispatcher.DisposeAsync();
        }
        logger?.Dispose();
        _executionLease?.Dispose();
    }

    private void EnsureRuntimePaths(ConnectorRuntimeOptions options)
    {
        // Shared execution is initialized once, so both local and online work use
        // the same stable state files for the lifetime of this host.
        options.LogFilePath = Path.Combine(_runtimeRootDirectory, "connector.jsonl");
        options.IdempotencyStatePath = Path.Combine(_runtimeRootDirectory, "idempotency-state.json");
    }

    private void EnsureExecutionInitialized()
    {
        lock (_executionSync)
        {
            ThrowIfDisposed();
            if (_dispatcher is not null)
            {
                return;
            }

            Directory.CreateDirectory(_runtimeRootDirectory);
            // One process owns recovery and execution for this durable state.
            // Releasing the handle (including process termination) permits the
            // next host to recover; no stale PID files or lock-file deletion.
            var lease = new FileStream(Path.Combine(_runtimeRootDirectory, "execution.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var logPath = Path.Combine(_runtimeRootDirectory, "connector.jsonl");
                var statePath = Path.Combine(_runtimeRootDirectory, "idempotency-state.json");
                _logger = new JsonLineConnectorLogger(
                    logPath,
                    writeToConsole: _writeLogsToConsole,
                    sink: line => LogEmitted?.Invoke(line));
                _idempotencyStore = new FileRequestIdempotencyStore(statePath);

                var publishers = new List<IJobStatusPublisher>
                {
                    new EventJobStatusPublisher(PublishJobStatus)
                };
                if (_includeConsolePublisher)
                {
                    publishers.Add(new ConsoleJobStatusPublisher());
                }

                _localStatusPublisher = new CompositeJobStatusPublisher(publishers.ToArray());
                _runner = new ConnectorJobRunner(
                    _providerAdapters,
                    _localStatusPublisher,
                    _idempotencyStore,
                    _logger,
                    _jobExecutors);
                _dispatcher = new ConnectorJobDispatcher(_runner, _executionGate, _localStatusPublisher);
                _executionLease = lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
    }

    private void PublishJobStatus(ConnectorJobStatusEnvelope status)
    {
        var handlers = JobStatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<ConnectorJobStatusEnvelope> handler in handlers.GetInvocationList())
        {
            try { handler(status); }
            catch { }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ConnectorRuntimeHost));
        }
    }

    private static ConnectorRuntimeOptions CloneOptions(ConnectorRuntimeOptions source)
    {
        return new ConnectorRuntimeOptions
        {
            ServerUrl = source.ServerUrl,
            DeviceId = source.DeviceId,
            DeviceToken = source.DeviceToken,
            AuthenticationMode = source.AuthenticationMode,
            SecureTokenPath = source.SecureTokenPath,
            SessionId = source.SessionId,
            HeartbeatSeconds = source.HeartbeatSeconds,
            PollIntervalSeconds = source.PollIntervalSeconds,
            PollBackoffMaxSeconds = source.PollBackoffMaxSeconds,
            StatusRefreshSeconds = source.StatusRefreshSeconds,
            EnableJobPolling = source.EnableJobPolling,
            RunDemoJobWhenIdle = source.RunDemoJobWhenIdle,
            EnableStatusShell = source.EnableStatusShell,
            IdempotencyStatePath = source.IdempotencyStatePath,
            LogFilePath = source.LogFilePath,
            AgentType = source.AgentType,
            ModuleScope = source.ModuleScope,
            Capabilities = [.. source.Capabilities],
            UpdateManifestUrl = source.UpdateManifestUrl,
            SmbAccess = source.SmbAccess is null
                ? null
                : new BootstrapSmbAccessDto
                {
                    Login = source.SmbAccess.Login,
                    Username = source.SmbAccess.Username,
                    Password = source.SmbAccess.Password,
                    ShareUnc = source.SmbAccess.ShareUnc,
                    SharePath = source.SmbAccess.SharePath
                }
        };
    }

    private sealed class EventJobStatusPublisher(Action<ConnectorJobStatusEnvelope> publish) : IJobStatusPublisher
    {
        public Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
        {
            publish(status);
            return Task.CompletedTask;
        }
    }
}
