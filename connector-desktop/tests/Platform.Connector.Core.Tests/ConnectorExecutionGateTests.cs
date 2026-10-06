using System.Text.Json;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class ConnectorExecutionGateTests
{
    [Fact]
    public async Task IdleDrain_ClosesAdmissionUntilResume()
    {
        var gate = new ConnectorExecutionGate();
        var drained = await gate.RequestDrainAsync(TimeSpan.Zero);
        Assert.Equal(ConnectorDrainPhase.ReadyToApply, drained.Phase);
        Assert.Null(gate.TryEnter());
        gate.Resume();
        using var work = gate.TryEnter();
        Assert.NotNull(work);
        Assert.Equal(1, gate.Snapshot.ActiveOperations);
    }

    [Fact]
    public async Task Drain_WaitsForEveryAcceptedOperation_AndLeaseDisposalIsIdempotent()
    {
        var gate = new ConnectorExecutionGate();
        var first = gate.TryEnter()!;
        var second = gate.TryEnter()!;
        var drain = gate.RequestDrainAsync(TimeSpan.FromSeconds(5));
        Assert.Null(gate.TryEnter());
        first.Dispose();
        first.Dispose();
        Assert.False(drain.IsCompleted);
        Assert.Equal(1, gate.Snapshot.ActiveOperations);
        second.Dispose();
        var result = await drain;
        Assert.Equal(new ConnectorDrainSnapshot(ConnectorDrainPhase.ReadyToApply, 0), result);
    }

    [Fact]
    public async Task Timeout_DoesNotCancelWorkOrBecomeReadyAfterLateCompletion()
    {
        var gate = new ConnectorExecutionGate();
        var work = gate.TryEnter()!;
        var result = await gate.RequestDrainAsync(TimeSpan.Zero);
        Assert.Equal(ConnectorDrainPhase.Blocked, result.Phase);
        Assert.Equal(1, result.ActiveOperations);
        work.Dispose();
        Assert.Equal(new ConnectorDrainSnapshot(ConnectorDrainPhase.Blocked, 0), gate.Snapshot);
        Assert.Null(gate.TryEnter());
        gate.Resume();
        using var next = gate.TryEnter();
        Assert.NotNull(next);
    }

    [Fact]
    public async Task Cancellation_BlocksApply_ExplicitRetryCanPrepareFinishedWork()
    {
        var gate = new ConnectorExecutionGate();
        var work = gate.TryEnter()!;
        using var cancelled = new CancellationTokenSource();
        var drain = gate.RequestDrainAsync(TimeSpan.FromSeconds(5), cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
        Assert.Equal(ConnectorDrainPhase.Blocked, gate.Snapshot.Phase);
        work.Dispose();
        Assert.Equal(ConnectorDrainPhase.Blocked, gate.Snapshot.Phase);
        Assert.Equal(ConnectorDrainPhase.ReadyToApply, (await gate.RequestDrainAsync(TimeSpan.Zero)).Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCancelledDrain_ClosesAdmissionWithoutCancellingAcceptedWork(bool active)
    {
        var gate = new ConnectorExecutionGate();
        var work = active ? gate.TryEnter() : null;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.RequestDrainAsync(TimeSpan.Zero, cancelled.Token));
        Assert.Equal(ConnectorDrainPhase.Blocked, gate.Snapshot.Phase);
        Assert.Equal(active ? 1 : 0, gate.Snapshot.ActiveOperations);
        Assert.Null(gate.TryEnter());
        work?.Dispose();
        Assert.Equal(new ConnectorDrainSnapshot(ConnectorDrainPhase.Blocked, 0), gate.Snapshot);
        gate.Resume();
        using var next = gate.TryEnter();
        Assert.NotNull(next);
    }

    [Fact]
    public async Task Resume_AbortsPendingDrain_WithoutReclosingAdmissionOnLateCompletion()
    {
        var gate = new ConnectorExecutionGate();
        var work = gate.TryEnter()!;
        var drain = gate.RequestDrainAsync(TimeSpan.FromSeconds(5));
        gate.Resume();
        Assert.Equal(ConnectorDrainPhase.Running, (await drain).Phase);
        work.Dispose();
        using var next = gate.TryEnter();
        Assert.NotNull(next);
        Assert.Equal(ConnectorDrainPhase.Running, gate.Snapshot.Phase);
    }

    [Fact]
    public async Task CancelledOldRequest_DoesNotInvalidateNewDrainAfterResume()
    {
        var gate = new ConnectorExecutionGate();
        var work = gate.TryEnter()!;
        using var cancelled = new CancellationTokenSource();
        var old = gate.RequestDrainAsync(TimeSpan.FromSeconds(5), cancelled.Token);
        cancelled.Cancel();
        gate.Resume();
        work.Dispose();
        Assert.Equal(ConnectorDrainPhase.ReadyToApply, (await gate.RequestDrainAsync(TimeSpan.Zero)).Phase);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.Equal(new ConnectorDrainSnapshot(ConnectorDrainPhase.ReadyToApply, 0), gate.Snapshot);
    }

    [Fact]
    public async Task ConcurrentDrain_IsRejectedWithoutChangingOriginalRequest()
    {
        var gate = new ConnectorExecutionGate();
        var work = gate.TryEnter()!;
        var drain = gate.RequestDrainAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RequestDrainAsync(TimeSpan.Zero));
        work.Dispose();
        Assert.Equal(ConnectorDrainPhase.ReadyToApply, (await drain).Phase);
    }

    [Fact]
    public async Task ProtectedDrain_JoinsExistingDrain_AndOldResumeCannotReopenAdmission()
    {
        var gate = new ConnectorExecutionGate();
        using var work = gate.TryEnter()!;
        var ordinaryDrain = gate.RequestDrainAsync(TimeSpan.FromSeconds(5));
        using var protectedDrain = gate.BeginProtectedDrain();

        var joined = gate.RequestDrainOrJoinAsync(Timeout.InfiniteTimeSpan);
        gate.Resume();

        Assert.Equal(ConnectorDrainPhase.Draining, gate.Snapshot.Phase);
        Assert.Null(gate.TryEnter());
        work.Dispose();

        Assert.Equal(ConnectorDrainPhase.ReadyToApply, (await joined).Phase);
        Assert.Equal(ConnectorDrainPhase.ReadyToApply, (await ordinaryDrain).Phase);
        Assert.Null(gate.TryEnter());

        protectedDrain.Dispose();
        gate.Resume();
        using var next = gate.TryEnter();
        Assert.NotNull(next);
    }

    [Fact]
    public async Task ProtectedDrain_RetriesBlockedDrain_AndWaitsForAcceptedWork()
    {
        var gate = new ConnectorExecutionGate();
        using var work = gate.TryEnter()!;
        var timedOut = await gate.RequestDrainAsync(TimeSpan.Zero);
        Assert.Equal(ConnectorDrainPhase.Blocked, timedOut.Phase);

        using var protectedDrain = gate.BeginProtectedDrain();
        var joined = gate.RequestDrainOrJoinAsync(Timeout.InfiniteTimeSpan);
        Assert.Equal(ConnectorDrainPhase.Draining, gate.Snapshot.Phase);
        Assert.Null(gate.TryEnter());
        work.Dispose();

        Assert.Equal(ConnectorDrainPhase.ReadyToApply, (await joined).Phase);
    }

    [Fact]
    public async Task Worker_DrainIncludesPendingPollAndTerminalPublication_ThenResumePollsAgain()
    {
        var gate = new ConnectorExecutionGate();
        var controlPlane = new ControlledControlPlane();
        var publisher = new ControlledPublisher();
        using var stop = new CancellationTokenSource();
        using var logger = new JsonLineConnectorLogger(null);
        var runner = new ConnectorJobRunner([new SuccessfulProvider()], publisher, logger: logger);
        var worker = new ConnectorWorker(controlPlane, runner, publisher, NullRequestIdempotencyStore.Instance, logger, gate);
        var running = worker.RunAsync(BuildOptions(), stop.Token);
        try
        {
            await controlPlane.FirstPollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var draining = gate.RequestDrainAsync(TimeSpan.FromSeconds(5));
            Assert.False(draining.IsCompleted);
            Assert.Equal(1, gate.Snapshot.ActiveOperations);
            controlPlane.FirstPollRelease.TrySetResult(CreateJob());
            await publisher.TerminalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(draining.IsCompleted);
            Assert.Equal(ConnectorDrainPhase.Draining, gate.Snapshot.Phase);
            publisher.TerminalRelease.TrySetResult();
            Assert.Equal(ConnectorDrainPhase.ReadyToApply, (await draining).Phase);
            Assert.Equal(JobStatus.Success, publisher.TerminalStatus);
            Assert.Null(gate.TryEnter());
            Assert.Equal(1, controlPlane.PollCalls);
            gate.Resume();
            await controlPlane.NextPollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(controlPlane.PollCalls >= 2);
        }
        finally
        {
            stop.Cancel();
            controlPlane.FirstPollRelease.TrySetResult(null);
            publisher.TerminalRelease.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Worker_PollFailureBackoffIsNotAcceptedWork()
    {
        var gate = new ConnectorExecutionGate();
        var controlPlane = new ControlledControlPlane();
        var publisher = new ControlledPublisher();
        using var stop = new CancellationTokenSource();
        using var logger = new JsonLineConnectorLogger(null);
        var runner = new ConnectorJobRunner([new SuccessfulProvider()], publisher, logger: logger);
        var worker = new ConnectorWorker(controlPlane, runner, publisher, NullRequestIdempotencyStore.Instance, logger, gate);
        var options = BuildOptions();
        options.PollIntervalSeconds = 60;
        options.PollBackoffMaxSeconds = 60;
        var running = worker.RunAsync(options, stop.Token);
        try
        {
            await controlPlane.FirstPollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var drain = gate.RequestDrainAsync(TimeSpan.FromSeconds(5));
            controlPlane.FirstPollRelease.TrySetException(new HttpRequestException("offline"));
            Assert.Equal(ConnectorDrainPhase.ReadyToApply, (await drain).Phase);
            Assert.Equal(0, gate.Snapshot.ActiveOperations);
        }
        finally
        {
            stop.Cancel();
            controlPlane.FirstPollRelease.TrySetResult(null);
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static ConnectorRuntimeOptions BuildOptions() => new()
    {
        DeviceId = "test-device", DeviceToken = "test-token", EnableJobPolling = true,
        PollIntervalSeconds = 1, PollBackoffMaxSeconds = 2, HeartbeatSeconds = 30
    };

    private static ConnectorJobEnvelope CreateJob() => new(
        1, "test-request", "test-module", CadProvider.Tekla, JobOperation.Export,
        DateTime.UtcNow, JsonSerializer.SerializeToElement(new { }));

    private sealed class SuccessfulProvider : IProviderAdapter
    {
        public CadProvider Provider => CadProvider.Tekla;
        public Task<ProviderExecutionResult> ExecuteAsync(ConnectorJobEnvelope job, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderExecutionResult(true, "ok"));
        }
    }

    private sealed class ControlledControlPlane : IConnectorControlPlaneClient
    {
        private int _pollCalls;
        public int PollCalls => Volatile.Read(ref _pollCalls);
        public TaskCompletionSource FirstPollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ConnectorJobEnvelope?> FirstPollRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NextPollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => Task.FromResult(new BootstrapResponseDto { Ok = true });
        public Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendJobStatusAsync(ConnectorRuntimeOptions options, ConnectorJobStatusEnvelope status, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _pollCalls) == 1)
            {
                FirstPollStarted.TrySetResult();
                return await FirstPollRelease.Task.WaitAsync(cancellationToken);
            }
            NextPollStarted.TrySetResult();
            return null;
        }
    }

    private sealed class ControlledPublisher : IJobStatusPublisher
    {
        public TaskCompletionSource TerminalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TerminalRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JobStatus? TerminalStatus { get; private set; }
        public async Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
        {
            if (status.Status != JobStatus.Success) { return; }
            TerminalStatus = status.Status;
            TerminalStarted.TrySetResult();
            await TerminalRelease.Task.WaitAsync(cancellationToken);
        }
    }
}
