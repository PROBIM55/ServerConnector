using System.Collections.Concurrent;
using System.Text.Json;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class ConnectorAgentDispatcherTests
{
    [Fact]
    public void ExecutionScope_EnforcesDeviceLocalAndProjectInvariants()
    {
        var local = ExecutionScope.DeviceLocal(ConnectorProductId.Structura);
        var project = ExecutionScope.Project(ConnectorProductId.Platform, " tenant-1 ", " project-1 ");

        Assert.Null(local.TenantId);
        Assert.Null(local.ProjectId);
        Assert.Equal("tenant-1", project.TenantId);
        Assert.Equal("project-1", project.ProjectId);
        var json = JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"productId\":\"platform\"", json);
        Assert.Contains("\"scopeKind\":\"project\"", json);
        var legacyOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var legacyJson = JsonSerializer.Serialize(
            BuildJob("legacy", "test.legacy", local) with { Scope = null },
            legacyOptions);
        Assert.False(legacyJson.Contains("scope", StringComparison.OrdinalIgnoreCase));
        Assert.Null(JsonSerializer.Deserialize<ConnectorJobEnvelope>(legacyJson, legacyOptions)?.Scope);
        Assert.Throws<ArgumentException>(() => new ExecutionScope(
            ConnectorProductId.Structura,
            "tenant-1",
            ConnectorScopeKind.DeviceLocal,
            null));
        Assert.Throws<ArgumentException>(() => new ExecutionScope(
            ConnectorProductId.Platform,
            null,
            ConnectorScopeKind.Project,
            "project-1"));
    }

    [Fact]
    public void SharedModuleCatalog_MapsOneExecutorRegistrationToItsProducts()
    {
        var descriptor = new SharedModuleDescriptor(
            "converters",
            "Конвертеры",
            [ConnectorProductId.Structura, ConnectorProductId.Platform],
            ["converter.fbx-glb", "converter.ifc-optimize"]);
        var catalog = new SharedModuleCatalog([descriptor]);

        Assert.True(catalog.TryGetByExecutor("CONVERTER.FBX-GLB", out var registered));
        Assert.Same(descriptor, registered);
        Assert.Equal(2, registered.ProductIds.Count);
    }

    [Fact]
    public async Task Host_SubmitLocalOffline_UsesSharedRunnerAndPropagatesScope()
    {
        var runtimeRoot = CreateRuntimeRoot();
        try
        {
            var executor = new BlockingFirstExecutor("converter.fbx-glb");
            await using var host = new ConnectorRuntimeHost(
                Array.Empty<IProviderAdapter>(),
                [executor],
                runtimeRootDirectory: runtimeRoot,
                localDeviceId: "offline-device");
            var statuses = new ConcurrentQueue<ConnectorJobStatusEnvelope>();
            host.JobStatusChanged += statuses.Enqueue;
            var scope = ExecutionScope.DeviceLocal(ConnectorProductId.Structura);

            var submitted = host.SubmitLocalAsync(BuildJob("local-first", executor.ExecutorId, scope));
            await executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.StopAsync();
            Assert.False(submitted.IsCompleted);
            executor.ReleaseFirst.TrySetResult();
            var terminal = await submitted.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(JobStatus.Success, terminal.Status);
            Assert.Equal(scope, terminal.Scope);
            Assert.Equal("offline-device", terminal.DeviceId);
            Assert.All(statuses, status => Assert.Equal(scope, status.Scope));
            Assert.Equal(new[] { "local-first" }, executor.StartOrder);
            Assert.False(host.IsRunning);
            Assert.True(File.Exists(Path.Combine(runtimeRoot, "idempotency-state.json")));
        }
        finally
        {
            DeleteRuntimeRoot(runtimeRoot);
        }
    }

    [Fact]
    public async Task Dispatcher_LocalAndRemoteJobs_RunOnOneFifoConsumer()
    {
        var gate = new ConnectorExecutionGate();
        var executor = new BlockingFirstExecutor("test.shared");
        var localPublisher = new RecordingPublisher();
        var remotePublisher = new RecordingPublisher(status => status.Status == JobStatus.Success);
        using var logger = new JsonLineConnectorLogger(null, writeToConsole: false);
        var runner = new ConnectorJobRunner(
            Array.Empty<IProviderAdapter>(),
            localPublisher,
            NullRequestIdempotencyStore.Instance,
            logger,
            [executor]);
        await using var dispatcher = new ConnectorJobDispatcher(runner, gate, localPublisher);
        var local = dispatcher.SubmitLocalAsync(
            "local-device",
            BuildJob("local-first", executor.ExecutorId, ExecutionScope.DeviceLocal(ConnectorProductId.Structura)));
        await executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        remotePublisher.OnMatchingStatus = stop.Cancel;
        var controlPlane = new SingleJobControlPlane(
            BuildJob("remote-second", executor.ExecutorId, ExecutionScope.Project(ConnectorProductId.Platform, "tenant", "project")));
        var worker = new ConnectorWorker(
            controlPlane,
            runner,
            remotePublisher,
            NullRequestIdempotencyStore.Instance,
            logger,
            gate,
            dispatcher);
        var remote = worker.RunAsync(BuildOptions(), stop.Token);
        await controlPlane.JobReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var draining = gate.RequestDrainAsync(TimeSpan.FromSeconds(5));
        Assert.False(draining.IsCompleted);
        Assert.Equal(2, gate.Snapshot.ActiveOperations);
        executor.ReleaseFirst.TrySetResult();

        Assert.Equal(JobStatus.Success, (await local.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        await remote.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConnectorDrainPhase.ReadyToApply, (await draining).Phase);
        Assert.Equal(new[] { "local-first", "remote-second" }, executor.StartOrder);
        Assert.Equal(1, executor.MaxConcurrency);
    }

    [Fact]
    public async Task Dispatcher_DrainWaitsForRunningAndQueuedLocalLeases()
    {
        var gate = new ConnectorExecutionGate();
        var executor = new BlockingFirstExecutor("test.drain");
        var publisher = new RecordingPublisher();
        using var logger = new JsonLineConnectorLogger(null, writeToConsole: false);
        var runner = new ConnectorJobRunner([], publisher, logger: logger, executors: [executor]);
        await using var dispatcher = new ConnectorJobDispatcher(runner, gate, publisher);

        var first = dispatcher.SubmitLocalAsync("device", BuildLocalJob("drain-1", executor.ExecutorId));
        await executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = dispatcher.SubmitLocalAsync("device", BuildLocalJob("drain-2", executor.ExecutorId));
        var draining = gate.RequestDrainAsync(TimeSpan.FromSeconds(5));

        Assert.False(draining.IsCompleted);
        Assert.Equal(2, gate.Snapshot.ActiveOperations);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = dispatcher.SubmitLocalAsync("device", BuildLocalJob("rejected", executor.ExecutorId));
        });
        executor.ReleaseFirst.TrySetResult();

        Assert.Equal(JobStatus.Success, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(JobStatus.Success, (await second.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(ConnectorDrainPhase.ReadyToApply, (await draining).Phase);
    }

    [Fact]
    public async Task Dispatcher_CancelsQueuedAndRunningJobsWithoutParallelExecution()
    {
        var gate = new ConnectorExecutionGate();
        var executor = new CancellationExecutor("test.cancel");
        var publisher = new RecordingPublisher();
        using var logger = new JsonLineConnectorLogger(null, writeToConsole: false);
        var runner = new ConnectorJobRunner([], publisher, logger: logger, executors: [executor]);
        await using var dispatcher = new ConnectorJobDispatcher(runner, gate, publisher);

        var blocker = dispatcher.SubmitLocalAsync("device", BuildLocalJob("blocker", executor.ExecutorId));
        await executor.BlockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var queuedCancellation = new CancellationTokenSource();
        var queued = dispatcher.SubmitLocalAsync(
            "device",
            BuildLocalJob("queued-cancel", executor.ExecutorId),
            queuedCancellation.Token);
        queuedCancellation.Cancel();
        executor.ReleaseBlocker.TrySetResult();

        Assert.Equal(JobStatus.Success, (await blocker.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(JobStatus.Cancelled, (await queued.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.DoesNotContain("queued-cancel", executor.StartedRequestIds);

        using var runningCancellation = new CancellationTokenSource();
        var running = dispatcher.SubmitLocalAsync(
            "device",
            BuildLocalJob("running-cancel", executor.ExecutorId),
            runningCancellation.Token);
        await executor.RunningStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runningCancellation.Cancel();

        Assert.Equal(JobStatus.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(1, executor.MaxConcurrency);
    }

    [Fact]
    public async Task Worker_ReconnectDeliveryUsesStoredTerminalWithoutSecondExecution()
    {
        var runtimeRoot = CreateRuntimeRoot();
        var statePath = Path.Combine(runtimeRoot, "idempotency.json");
        var options = BuildOptions();
        try
        {
            var job = BuildJob(
                "remote-duplicate",
                "test.duplicate",
                ExecutionScope.Project(ConnectorProductId.Platform, "tenant", "project"));
            var storedStatus = new ConnectorJobStatusEnvelope(
                job.SchemaVersion,
                job.RequestId,
                "remote-device",
                job.ModuleId,
                job.Provider,
                JobStatus.Success,
                DateTime.UtcNow,
                ExecutorId: job.ExecutorId,
                Scope: job.Scope);
            var store = new FileRequestIdempotencyStore(statePath);
            await store.SaveTerminalAsync(
                storedStatus,
                published: false,
                CancellationToken.None,
                ConnectorRemoteAuthority.Normalize(options.ServerUrl));
            var executor = new RecordingExecutor(job.ExecutorId!);
            var publisher = new RecordingPublisher(status => status.Status == JobStatus.Success);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            publisher.OnMatchingStatus = stop.Cancel;
            using var logger = new JsonLineConnectorLogger(null, writeToConsole: false);
            var runner = new ConnectorJobRunner([], publisher, store, logger, [executor]);
            var gate = new ConnectorExecutionGate();
            await using var dispatcher = new ConnectorJobDispatcher(runner, gate, publisher);
            var worker = new ConnectorWorker(
                new SingleJobControlPlane(job),
                runner,
                publisher,
                store,
                logger,
                gate,
                dispatcher);

            await worker.RunAsync(options, stop.Token).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, executor.CallCount);
            Assert.Single(publisher.Statuses);
            Assert.Equal(job.Scope, publisher.Statuses.Single().Scope);
            Assert.True((await store.TryGetAsync(job.RequestId, CancellationToken.None))!.Published);
        }
        finally
        {
            DeleteRuntimeRoot(runtimeRoot);
        }
    }

    private static ConnectorJobEnvelope BuildLocalJob(string requestId, string executorId)
        => BuildJob(requestId, executorId, ExecutionScope.DeviceLocal(ConnectorProductId.Structura));

    private static ConnectorJobEnvelope BuildJob(string requestId, string executorId, ExecutionScope scope)
        => new(
            SchemaVersion: 2,
            RequestId: requestId,
            ModuleId: "test-module",
            Provider: CadProvider.AutoCad,
            Operation: JobOperation.Export,
            CreatedAtUtc: DateTime.UtcNow,
            Payload: JsonSerializer.SerializeToElement(new { value = 1 }),
            CorrelationId: $"corr-{requestId}",
            ExecutorId: executorId,
            Scope: scope);

    private static ConnectorRuntimeOptions BuildOptions() => new()
    {
        DeviceId = "remote-device",
        DeviceToken = "test-token",
        PollIntervalSeconds = 1,
        PollBackoffMaxSeconds = 2,
        HeartbeatSeconds = 30
    };

    private static string CreateRuntimeRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"connector-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRuntimeRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class RecordingPublisher(Func<ConnectorJobStatusEnvelope, bool>? match = null) : IJobStatusPublisher
    {
        public ConcurrentQueue<ConnectorJobStatusEnvelope> Statuses { get; } = new();
        public Action? OnMatchingStatus { get; set; }

        public Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
        {
            Statuses.Enqueue(status);
            if (match?.Invoke(status) == true)
            {
                OnMatchingStatus?.Invoke();
            }
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExecutor(string executorId) : IConnectorJobExecutor
    {
        public string ExecutorId => executorId;
        public int CallCount { get; private set; }
        public ConnectorJobEnvelope? ReceivedJob { get; private set; }

        public Task<ConnectorExecutionResult> ExecuteAsync(
            ConnectorJobEnvelope job,
            Func<ConnectorExecutionProgress, CancellationToken, Task>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            ReceivedJob = job;
            return Task.FromResult(new ConnectorExecutionResult(true, "done"));
        }
    }

    private sealed class BlockingFirstExecutor(string executorId) : IConnectorJobExecutor
    {
        private int _concurrency;
        public string ExecutorId => executorId;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> StartOrder { get; } = [];
        public int MaxConcurrency { get; private set; }

        public async Task<ConnectorExecutionResult> ExecuteAsync(
            ConnectorJobEnvelope job,
            Func<ConnectorExecutionProgress, CancellationToken, Task>? progress,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _concurrency);
            MaxConcurrency = Math.Max(MaxConcurrency, current);
            StartOrder.Add(job.RequestId);
            try
            {
                if (job.RequestId == "local-first" || job.RequestId == "drain-1")
                {
                    FirstStarted.TrySetResult();
                    await ReleaseFirst.Task.WaitAsync(cancellationToken);
                }
                return new ConnectorExecutionResult(true, "done");
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }
    }

    private sealed class CancellationExecutor(string executorId) : IConnectorJobExecutor
    {
        private int _concurrency;
        public string ExecutorId => executorId;
        public TaskCompletionSource BlockerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBlocker { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RunningStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> StartedRequestIds { get; } = new();
        public int MaxConcurrency { get; private set; }

        public async Task<ConnectorExecutionResult> ExecuteAsync(
            ConnectorJobEnvelope job,
            Func<ConnectorExecutionProgress, CancellationToken, Task>? progress,
            CancellationToken cancellationToken)
        {
            StartedRequestIds.Enqueue(job.RequestId);
            var current = Interlocked.Increment(ref _concurrency);
            MaxConcurrency = Math.Max(MaxConcurrency, current);
            try
            {
                if (job.RequestId == "blocker")
                {
                    BlockerStarted.TrySetResult();
                    await ReleaseBlocker.Task.WaitAsync(cancellationToken);
                }
                else if (job.RequestId == "running-cancel")
                {
                    RunningStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return new ConnectorExecutionResult(true, "done");
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }
    }

    private sealed class SingleJobControlPlane(ConnectorJobEnvelope job) : IConnectorControlPlaneClient
    {
        private int _pollCount;
        public TaskCompletionSource JobReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => Task.FromResult(new BootstrapResponseDto { Ok = true });
        public Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendJobStatusAsync(ConnectorRuntimeOptions options, ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _pollCount) == 1)
            {
                JobReturned.TrySetResult();
                return Task.FromResult<ConnectorJobEnvelope?>(job);
            }
            return Task.FromResult<ConnectorJobEnvelope?>(null);
        }
    }
}
