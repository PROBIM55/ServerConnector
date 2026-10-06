using System.Net;
using System.Net.Http;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class ConnectorRuntimeSupervisorTests
{
    private static ConnectorRuntimeOptions BuildOptions() => new()
    {
        DeviceId = "device-1",
        DeviceToken = "token",
        EnableJobPolling = true,
        PollIntervalSeconds = 1,
        PollBackoffMaxSeconds = 2,
        HeartbeatSeconds = 30
    };

    private static (ConnectorRuntimeSupervisor Supervisor, ConnectorRuntimeState State, List<TimeSpan> Delays) BuildSupervisor(
        ScriptedControlPlane controlPlane,
        ConnectorSupervisorOptions? supervisorOptions = null)
    {
        var state = new ConnectorRuntimeState();
        controlPlane.RuntimeState = state;
        var logger = new JsonLineConnectorLogger(null, writeToConsole: false);
        var publisher = new NullPublisher();
        var store = new InMemoryStore();
        var runner = new ConnectorJobRunner(
            new[] { new FakeProvider() },
            publisher,
            store,
            logger);
        var worker = new ConnectorWorker(controlPlane, runner, publisher, store, logger);

        var delays = new List<TimeSpan>();
        var options = supervisorOptions ?? new ConnectorSupervisorOptions();
        options.DelayAsync = (delay, ct) =>
        {
            lock (delays)
            {
                delays.Add(delay);
            }

            controlPlane.OnDelay(delay);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        var supervisor = new ConnectorRuntimeSupervisor(controlPlane, worker, state, logger, options);
        return (supervisor, state, delays);
    }

    [Fact]
    public async Task Supervisor_OnSessionConflict_ReBootstrapsAndResumes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var controlPlane = new ScriptedControlPlane
        {
            // Первый poll после первого bootstrap → 409; дальше — пусто.
            PollBehavior = (calls, cp) =>
            {
                if (calls == 1)
                {
                    throw new SessionConflictException("superseded", HttpStatusCode.Conflict);
                }

                // Второй успешный poll возможен только после re-bootstrap → done.
                if (cp.BootstrapCalls >= 2 && calls >= 2)
                {
                    cp.RequestStop();
                }

                return null;
            }
        };
        controlPlane.StopAction = cts.Cancel;

        var (supervisor, state, delays) = BuildSupervisor(controlPlane);

        var finalPhase = await supervisor.RunAsync(BuildOptions(), cts.Token);

        Assert.True(controlPlane.BootstrapCalls >= 2, $"Expected re-bootstrap, got {controlPlane.BootstrapCalls} bootstraps.");
        Assert.Equal(ConnectorRuntimePhase.Stopped, finalPhase);
        Assert.Equal(ConnectorRuntimePhase.Stopped, state.GetSnapshot().Phase);
        Assert.Contains(delays, d => d == TimeSpan.FromSeconds(2)); // backoff после конфликта
        Assert.Contains(ConnectorRuntimePhase.Reconnecting, controlPlane.PhasesSeenAtBootstrap);
    }

    [Fact]
    public async Task Supervisor_WhenServerUnreachableAtStart_KeepsRetryingBootstrapWithBackoff()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var controlPlane = new ScriptedControlPlane
        {
            HealthBehavior = (calls, cp) =>
            {
                if (calls <= 3)
                {
                    throw new HttpRequestException("connection refused");
                }
            },
            PollBehavior = (calls, cp) =>
            {
                cp.RequestStop();
                return null;
            }
        };
        controlPlane.StopAction = cts.Cancel;

        var (supervisor, state, delays) = BuildSupervisor(controlPlane);

        var finalPhase = await supervisor.RunAsync(BuildOptions(), cts.Token);

        Assert.Equal(ConnectorRuntimePhase.Stopped, finalPhase);
        Assert.Equal(1, controlPlane.BootstrapCalls); // bootstrap дошёл только после успешного health
        Assert.True(controlPlane.HealthCalls >= 4);
        // Экспоненциальный backoff: 2 → 4 → 8.
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) },
            delays.Take(3).ToArray());
        Assert.Contains(ConnectorRuntimePhase.WaitingForServer, controlPlane.PhasesSeenAtDelay);
    }

    [Fact]
    public async Task Supervisor_OnTokenRejectedAtBootstrap_StopsTerminallyWithoutRetries()
    {
        var controlPlane = new ScriptedControlPlane
        {
            BootstrapBehavior = (calls, cp) => throw new TokenRejectedException("bad token", HttpStatusCode.Unauthorized)
        };

        var (supervisor, state, delays) = BuildSupervisor(controlPlane);

        var finalPhase = await supervisor.RunAsync(BuildOptions(), CancellationToken.None);

        Assert.Equal(ConnectorRuntimePhase.TokenInvalid, finalPhase);
        Assert.Equal(ConnectorRuntimePhase.TokenInvalid, state.GetSnapshot().Phase);
        Assert.Equal(1, controlPlane.BootstrapCalls); // никаких повторов по мёртвому токену
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Supervisor_OnTokenRejectedDuringRun_StopsWithTokenInvalid()
    {
        var controlPlane = new ScriptedControlPlane
        {
            PollBehavior = (calls, cp) => throw new TokenRejectedException("revoked", HttpStatusCode.Forbidden)
        };

        var (supervisor, state, _) = BuildSupervisor(controlPlane);

        var finalPhase = await supervisor.RunAsync(BuildOptions(), CancellationToken.None);

        Assert.Equal(ConnectorRuntimePhase.TokenInvalid, finalPhase);
        Assert.Equal(1, controlPlane.BootstrapCalls);
        Assert.Equal(ConnectorRuntimePhase.TokenInvalid, state.GetSnapshot().Phase);
    }

    [Fact]
    public async Task Supervisor_BackoffIsCappedAtMaxBackoff()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var controlPlane = new ScriptedControlPlane
        {
            HealthBehavior = (calls, cp) => throw new HttpRequestException("offline")
        };
        controlPlane.StopAction = cts.Cancel;
        controlPlane.StopAfterDelays = 6;

        var supervisorOptions = new ConnectorSupervisorOptions
        {
            InitialBackoff = TimeSpan.FromSeconds(2),
            MaxBackoff = TimeSpan.FromSeconds(8)
        };

        var (supervisor, _, delays) = BuildSupervisor(controlPlane, supervisorOptions);

        await supervisor.RunAsync(BuildOptions(), cts.Token);

        // 2 → 4 → 8 → 8 → 8 → 8 (cap).
        Assert.True(delays.Count >= 5, $"Expected >=5 delays, got {delays.Count}");
        Assert.Equal(TimeSpan.FromSeconds(2), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(4), delays[1]);
        Assert.Equal(TimeSpan.FromSeconds(8), delays[2]);
        Assert.All(delays.Skip(2), d => Assert.Equal(TimeSpan.FromSeconds(8), d));
    }

    [Fact]
    public async Task Supervisor_AfterStableRun_ResetsBackoffOnNextConflict()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var controlPlane = new ScriptedControlPlane
        {
            PollBehavior = (calls, cp) =>
            {
                // Каждый первый poll каждой сессии → конфликт; третий bootstrap → stop.
                if (cp.BootstrapCalls >= 3)
                {
                    cp.RequestStop();
                    return null;
                }

                throw new SessionConflictException("superseded", HttpStatusCode.Conflict);
            }
        };
        controlPlane.StopAction = cts.Cancel;

        var supervisorOptions = new ConnectorSupervisorOptions
        {
            // Порог «стабильной» работы 0 — каждый прогон worker'а считается стабильным,
            // значит backoff каждый раз сбрасывается к Initial.
            StableRunThreshold = TimeSpan.Zero
        };

        var (supervisor, _, delays) = BuildSupervisor(controlPlane, supervisorOptions);

        await supervisor.RunAsync(BuildOptions(), cts.Token);

        Assert.True(delays.Count >= 2);
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromSeconds(2), d));
    }

    [Fact]
    public async Task Supervisor_StopDuringWaitingForServer_EndsInStoppedPhase()
    {
        using var cts = new CancellationTokenSource();
        var controlPlane = new ScriptedControlPlane
        {
            HealthBehavior = (calls, cp) => throw new HttpRequestException("offline")
        };
        controlPlane.StopAction = cts.Cancel;
        controlPlane.StopAfterDelays = 2;

        var (supervisor, state, _) = BuildSupervisor(controlPlane);

        var finalPhase = await supervisor.RunAsync(BuildOptions(), cts.Token);

        Assert.Equal(ConnectorRuntimePhase.Stopped, finalPhase);
        Assert.Equal(ConnectorRuntimePhase.Stopped, state.GetSnapshot().Phase);
    }

    // ════════════════════════ Test doubles ════════════════════════

    private sealed class ScriptedControlPlane : IConnectorControlPlaneClient
    {
        private int _healthCalls;
        private int _bootstrapCalls;
        private int _pollCalls;
        private int _delayCalls;
        private bool _stopRequested;

        public ConnectorRuntimeState? RuntimeState { get; set; }
        public Action? StopAction { get; set; }
        public int? StopAfterDelays { get; set; }

        public Action<int, ScriptedControlPlane>? HealthBehavior { get; set; }
        public Func<int, ScriptedControlPlane, BootstrapResponseDto>? BootstrapBehavior { get; set; }
        public Func<int, ScriptedControlPlane, ConnectorJobEnvelope?>? PollBehavior { get; set; }

        public int HealthCalls => _healthCalls;
        public int BootstrapCalls => _bootstrapCalls;
        public int PollCalls => _pollCalls;

        public List<ConnectorRuntimePhase> PhasesSeenAtBootstrap { get; } = new();
        public List<ConnectorRuntimePhase> PhasesSeenAtDelay { get; } = new();

        public void RequestStop()
        {
            if (_stopRequested)
            {
                return;
            }

            _stopRequested = true;
            StopAction?.Invoke();
        }

        public void OnDelay(TimeSpan _)
        {
            _delayCalls++;
            if (RuntimeState is not null)
            {
                PhasesSeenAtDelay.Add(RuntimeState.GetSnapshot().Phase);
            }

            if (StopAfterDelays is int limit && _delayCalls >= limit)
            {
                RequestStop();
            }
        }

        public Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            var calls = Interlocked.Increment(ref _healthCalls);
            HealthBehavior?.Invoke(calls, this);
            return Task.CompletedTask;
        }

        public Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            var calls = Interlocked.Increment(ref _bootstrapCalls);
            if (RuntimeState is not null)
            {
                PhasesSeenAtBootstrap.Add(RuntimeState.GetSnapshot().Phase);
            }

            if (BootstrapBehavior is not null)
            {
                return Task.FromResult(BootstrapBehavior(calls, this));
            }

            return Task.FromResult(new BootstrapResponseDto
            {
                Ok = true,
                DeviceId = options.DeviceId,
                SessionId = $"session-{calls}",
                HeartbeatSeconds = 30
            });
        }

        public Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendJobStatusAsync(ConnectorRuntimeOptions options, ConnectorJobStatusEnvelope status, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            var calls = Interlocked.Increment(ref _pollCalls);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PollBehavior?.Invoke(calls, this));
        }
    }

    private sealed class NullPublisher : IJobStatusPublisher
    {
        public Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InMemoryStore : IRequestIdempotencyStore
    {
        private readonly Dictionary<string, StoredTerminalStatus> _items = new(StringComparer.OrdinalIgnoreCase);

        public Task<StoredTerminalStatus?> TryGetAsync(string requestId, CancellationToken cancellationToken)
        {
            _items.TryGetValue(requestId, out var value);
            return Task.FromResult(value);
        }

        public Task SaveTerminalAsync(ConnectorJobStatusEnvelope envelope, bool published, CancellationToken cancellationToken)
        {
            _items[envelope.RequestId] = new StoredTerminalStatus(envelope, published, DateTime.UtcNow);
            return Task.CompletedTask;
        }

        public Task MarkPublishedAsync(string requestId, CancellationToken cancellationToken)
        {
            if (_items.TryGetValue(requestId, out var existing))
            {
                _items[requestId] = existing with { Published = true, SavedAtUtc = DateTime.UtcNow };
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeProvider : IProviderAdapter
    {
        public CadProvider Provider => CadProvider.AutoCad;

        public Task<ProviderExecutionResult> ExecuteAsync(ConnectorJobEnvelope job, CancellationToken cancellationToken)
            => Task.FromResult(new ProviderExecutionResult(IsSuccess: true, Message: "ok"));
    }
}
