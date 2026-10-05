namespace Platform.Connector.Core;

public sealed class ConnectorRuntimeState
{
    private readonly object _gate = new();
    private readonly HashSet<string> _providers = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _startedAtUtc;
    private DateTime _lastStatusAtUtc;
    private DateTime _lastHeartbeatAtUtc;
    private DateTime _lastPollAtUtc;
    private string? _lastError;
    private string? _lastHeartbeatError;
    private string? _lastPollError;
    private bool _isRunning;
    private bool _serverHealthy;
    private bool _bootstrapSuccessful;
    private string? _sessionId;
    private string? _lastRequestId;
    private string? _lastModuleId;
    private string? _lastProvider;
    private string? _lastJobStatus;
    private string? _lastJobMessage;
    private ConnectorRuntimePhase _phase = ConnectorRuntimePhase.Stopped;
    private DateTime _phaseChangedAtUtc;
    private int _reconnectAttempts;
    private DateTime _nextRetryAtUtc;

    public void RegisterProvider(CadProvider provider)
    {
        lock (_gate)
        {
            _providers.Add(provider.ToString());
        }
    }

    public void MarkPhase(ConnectorRuntimePhase phase, string? error = null)
    {
        lock (_gate)
        {
            if (_phase != phase)
            {
                _phase = phase;
                _phaseChangedAtUtc = DateTime.UtcNow;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                _lastError = error;
            }

            if (phase == ConnectorRuntimePhase.Connected)
            {
                _reconnectAttempts = 0;
                _nextRetryAtUtc = default;
                _lastError = null;
            }
        }
    }

    public void MarkRetryScheduled(int attempt, DateTime nextRetryAtUtc)
    {
        lock (_gate)
        {
            _reconnectAttempts = attempt;
            _nextRetryAtUtc = nextRetryAtUtc;
        }
    }

    public void MarkWorkerStarted()
    {
        lock (_gate)
        {
            _isRunning = true;
            _startedAtUtc = DateTime.UtcNow;
        }
    }

    public void MarkWorkerStopped()
    {
        lock (_gate)
        {
            _isRunning = false;
        }
    }

    public void MarkHealthCheck(bool success, string? error = null)
    {
        lock (_gate)
        {
            _serverHealthy = success;
            if (!success)
            {
                _lastError = error;
            }
        }
    }

    public void MarkBootstrap(string? sessionId, bool success, string? error = null)
    {
        lock (_gate)
        {
            _bootstrapSuccessful = success;
            _sessionId = sessionId;
            if (!success)
            {
                _lastError = error;
            }
        }
    }

    public void MarkHeartbeat(bool success, string? error = null)
    {
        lock (_gate)
        {
            if (success)
            {
                _lastHeartbeatAtUtc = DateTime.UtcNow;
                _lastHeartbeatError = null;
            }
            else
            {
                _lastHeartbeatError = error;
                _lastError = error;
            }
        }
    }

    public void MarkPollResult(ConnectorJobEnvelope? job, string? error = null)
    {
        lock (_gate)
        {
            _lastPollAtUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(error))
            {
                _lastPollError = error;
                _lastError = error;
                return;
            }

            _lastPollError = null;
            if (job is null)
            {
                return;
            }

            _lastRequestId = job.RequestId;
            _lastModuleId = job.ModuleId;
            _lastProvider = job.Provider.ToString();
        }
    }

    public void MarkStatus(ConnectorJobStatusEnvelope status)
    {
        lock (_gate)
        {
            _lastStatusAtUtc = DateTime.UtcNow;
            _lastRequestId = status.RequestId;
            _lastModuleId = status.ModuleId;
            _lastProvider = status.Provider.ToString();
            _lastJobStatus = status.Status.ToString();
            _lastJobMessage = status.Message;
        }
    }

    public void MarkStatusPublishError(string error)
    {
        lock (_gate)
        {
            _lastError = error;
        }
    }

    public ConnectorRuntimeSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new ConnectorRuntimeSnapshot(
                IsRunning: _isRunning,
                StartedAtUtc: _startedAtUtc,
                ServerHealthy: _serverHealthy,
                BootstrapSuccessful: _bootstrapSuccessful,
                SessionId: _sessionId,
                LastHeartbeatAtUtc: _lastHeartbeatAtUtc,
                LastHeartbeatError: _lastHeartbeatError,
                LastPollAtUtc: _lastPollAtUtc,
                LastPollError: _lastPollError,
                LastStatusAtUtc: _lastStatusAtUtc,
                LastRequestId: _lastRequestId,
                LastModuleId: _lastModuleId,
                LastProvider: _lastProvider,
                LastJobStatus: _lastJobStatus,
                LastJobMessage: _lastJobMessage,
                LastError: _lastError,
                Providers: _providers.OrderBy(x => x).ToArray(),
                Phase: _phase,
                PhaseChangedAtUtc: _phaseChangedAtUtc,
                ReconnectAttempts: _reconnectAttempts,
                NextRetryAtUtc: _nextRetryAtUtc);
        }
    }
}

public sealed record ConnectorRuntimeSnapshot(
    bool IsRunning,
    DateTime StartedAtUtc,
    bool ServerHealthy,
    bool BootstrapSuccessful,
    string? SessionId,
    DateTime LastHeartbeatAtUtc,
    string? LastHeartbeatError,
    DateTime LastPollAtUtc,
    string? LastPollError,
    DateTime LastStatusAtUtc,
    string? LastRequestId,
    string? LastModuleId,
    string? LastProvider,
    string? LastJobStatus,
    string? LastJobMessage,
    string? LastError,
    IReadOnlyList<string> Providers,
    ConnectorRuntimePhase Phase = ConnectorRuntimePhase.Stopped,
    DateTime PhaseChangedAtUtc = default,
    int ReconnectAttempts = 0,
    DateTime NextRetryAtUtc = default);
