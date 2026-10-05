using System.Collections.Concurrent;
using System.Text.Json;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class LocalQueueRecoveryTests
{
    [Fact]
    public async Task SecondHostCannotRecoverSameQueueUntilOwnerIsDisposed()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var originalJob = BuildJob("single-owner", ConnectorProductId.Structura);
            var store = new FileRequestIdempotencyStore(Path.Combine(root, "idempotency-state.json"));
            await store.SaveLocalQueuedAsync("device-a", originalJob, CancellationToken.None);
            var firstExecutor = new RecordingExecutor();
            var secondExecutor = new RecordingExecutor();
            await using var first = new ConnectorRuntimeHost([], [firstExecutor], runtimeRootDirectory: root, localDeviceId: "device-a");
            await using var second = new ConnectorRuntimeHost([], [secondExecutor], runtimeRootDirectory: root, localDeviceId: "device-a");
            await first.PrepareLocalAsync();
            await Assert.ThrowsAsync<IOException>(() => second.PrepareLocalAsync());
            await Assert.ThrowsAsync<IOException>(() => second.RecoverLocalAsync());
            Assert.Empty(firstExecutor.RequestIds);
            Assert.Empty(secondExecutor.RequestIds);
            await first.DisposeAsync();
            var prepared = await second.PrepareLocalAsync();
            Assert.Equal("single-owner", Assert.Single(prepared.Jobs).Job.RequestId);
            await second.RecoverLocalAsync();
            await second.SubmitLocalAsync(BuildJob("after-recovery", ConnectorProductId.Structura)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(firstExecutor.RequestIds);
            Assert.Equal(new[] { "single-owner", "after-recovery" }, secondExecutor.RequestIds);
        }
        finally { DeleteRuntimeRoot(root); }
    }

    [Fact]
    public async Task FailedInitializationReleasesLeaseAndPreservesCorruptState()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var statePath = Path.Combine(root, "idempotency-state.json");
            await File.WriteAllTextAsync(statePath, "{invalid-state}");
            await using var first = new ConnectorRuntimeHost([], runtimeRootDirectory: root);
            await using var second = new ConnectorRuntimeHost([], runtimeRootDirectory: root);
            await Assert.ThrowsAsync<InvalidDataException>(() => first.PrepareLocalAsync());
            await Assert.ThrowsAsync<InvalidDataException>(() => second.PrepareLocalAsync());
            Assert.Equal("{invalid-state}", await File.ReadAllTextAsync(statePath));
        }
        finally { DeleteRuntimeRoot(root); }
    }

    [Fact]
    public async Task QueuedJobs_RecoverOnSharedFifo_AndKeepArtifactResults()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var statePath = Path.Combine(root, "idempotency-state.json");
            var first = BuildJob("queued-1", ConnectorProductId.Structura);
            var second = BuildJob("queued-2", ConnectorProductId.Platform);
            var afterRecovery = BuildJob("new-3", ConnectorProductId.Structura);
            var initialStore = new FileRequestIdempotencyStore(statePath);
            await initialStore.SaveLocalQueuedAsync("device-a", first, CancellationToken.None);
            await initialStore.SaveLocalQueuedAsync("device-a", second, CancellationToken.None);
            var executor = new RecordingExecutor();

            await using (var host = new ConnectorRuntimeHost(
                             [],
                             [executor],
                             runtimeRootDirectory: root,
                             localDeviceId: "device-a"))
            {
                var prepared = await host.PrepareLocalAsync();
                Assert.Equal("device-a", prepared.DeviceId);
                Assert.Equal(new[] { "queued-1", "queued-2" }, prepared.Jobs.Select(item => item.Job.RequestId));
                Assert.All(prepared.Jobs, item => Assert.Equal(JobStatus.Queued, item.LastStatus.Status));
                Assert.Empty(executor.RequestIds);
                await host.RecoverLocalAsync();
                var terminal = await host.SubmitLocalAsync(afterRecovery).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(JobStatus.Success, terminal.Status);
            }

            Assert.Equal(new[] { "queued-1", "queued-2", "new-3" }, executor.RequestIds);
            var reloaded = new FileRequestIdempotencyStore(statePath);
            Assert.Empty(await reloaded.GetLocalJobsAsync(CancellationToken.None));
            var recoveredResult = await reloaded.TryGetAsync(first.RequestId, CancellationToken.None);
            Assert.NotNull(recoveredResult);
            Assert.True(recoveredResult!.Published);
            Assert.Equal(first.Scope, recoveredResult.Envelope.Scope);
            Assert.Equal(first.Scope, recoveredResult.SourceJob!.Scope);
            Assert.Equal(
                "artifact-queued-1",
                recoveredResult.Envelope.Result!.Value
                    .GetProperty("artifactReceipt")
                    .GetProperty("artifactId")
                    .GetString());
        }
        finally
        {
            DeleteRuntimeRoot(root);
        }
    }

    [Fact]
    public async Task RunningJob_AfterRestartBecomesInterruptedWithoutSecondExecution()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var statePath = Path.Combine(root, "idempotency-state.json");
            var interruptedJob = BuildJob("running-before-restart", ConnectorProductId.Platform);
            var store = new FileRequestIdempotencyStore(statePath);
            await store.SaveLocalQueuedAsync("device-a", interruptedJob, CancellationToken.None);
            await store.MarkLocalRunningAsync(interruptedJob.RequestId, CancellationToken.None);
            var executor = new RecordingExecutor();
            var statuses = new ConcurrentQueue<ConnectorJobStatusEnvelope>();

            await using (var host = new ConnectorRuntimeHost(
                             [],
                             [executor],
                             runtimeRootDirectory: root,
                             localDeviceId: "device-a"))
            {
                host.JobStatusChanged += statuses.Enqueue;
                var prepared = await host.PrepareLocalAsync();
                var preparedInterrupted = Assert.Single(prepared.Jobs);
                Assert.Equal(JobStatus.Interrupted, preparedInterrupted.LastStatus.Status);
                Assert.Equal(StoredLocalJobState.Running, preparedInterrupted.PersistedState);
                Assert.Empty(executor.RequestIds);
                await host.SubmitLocalAsync(BuildJob("after-restart", ConnectorProductId.Structura))
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.Equal(new[] { "after-restart" }, executor.RequestIds);
            var interrupted = Assert.Single(
                statuses.Where(status => status.RequestId == interruptedJob.RequestId));
            Assert.Equal(JobStatus.Interrupted, interrupted.Status);
            Assert.Equal("CONNECTOR_EXECUTION_INTERRUPTED_REVIEW_REQUIRED", interrupted.ErrorCode);
            Assert.Contains("Verify external effects", interrupted.Message);

            var reloaded = new FileRequestIdempotencyStore(statePath);
            var persisted = await reloaded.TryGetAsync(interruptedJob.RequestId, CancellationToken.None);
            Assert.NotNull(persisted);
            Assert.Equal(JobStatus.Interrupted, persisted!.Envelope.Status);
            Assert.Equal(interruptedJob.Scope, persisted.SourceJob!.Scope);
            Assert.Empty(await reloaded.GetLocalJobsAsync(CancellationToken.None));
        }
        finally
        {
            DeleteRuntimeRoot(root);
        }
    }

    [Fact]
    public async Task ForeignDeviceQueue_IsNotRecoveredOrReassigned()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var statePath = Path.Combine(root, "idempotency-state.json");
            var foreign = BuildJob("foreign-job", ConnectorProductId.Platform);
            var store = new FileRequestIdempotencyStore(statePath);
            await store.SaveLocalQueuedAsync("device-a", foreign, CancellationToken.None);
            var executor = new RecordingExecutor();

            await using (var host = new ConnectorRuntimeHost(
                             [],
                             [executor],
                             runtimeRootDirectory: root,
                             localDeviceId: "device-b"))
            {
                Assert.Empty((await host.PrepareLocalAsync()).Jobs);
                await host.SubmitLocalAsync(BuildJob("owned-job", ConnectorProductId.Structura))
                    .WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.SubmitLocalAsync(foreign));
            }

            Assert.Equal(new[] { "owned-job" }, executor.RequestIds);
            var remaining = await new FileRequestIdempotencyStore(statePath)
                .GetLocalJobsAsync(CancellationToken.None);
            Assert.Equal("foreign-job", Assert.Single(remaining).Job.RequestId);
            Assert.Equal("device-a", remaining[0].DeviceId);
        }
        finally
        {
            DeleteRuntimeRoot(root);
        }
    }

    [Fact]
    public async Task RecoveryRejectedByDrain_CanResumeWithoutDuplicates_AndPrecedesNewSubmit()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var statePath = Path.Combine(root, "idempotency-state.json");
            var recovered = BuildJob("queued-before-drain", ConnectorProductId.Platform);
            var store = new FileRequestIdempotencyStore(statePath);
            await store.SaveLocalQueuedAsync("device-a", recovered, CancellationToken.None);
            var executor = new RecordingExecutor();

            await using var host = new ConnectorRuntimeHost(
                [],
                [executor],
                runtimeRootDirectory: root,
                localDeviceId: "device-a");
            var drained = await host.RequestDrainAsync(TimeSpan.Zero);
            Assert.Equal(ConnectorDrainPhase.ReadyToApply, drained.Phase);

            await Assert.ThrowsAsync<InvalidOperationException>(() => host.RecoverLocalAsync());
            Assert.Empty(executor.RequestIds);
            Assert.Equal("queued-before-drain", Assert.Single((await host.PrepareLocalAsync()).Jobs).Job.RequestId);

            host.Resume();
            await host.RecoverLocalAsync();
            await host.SubmitLocalAsync(BuildJob("submitted-after-resume", ConnectorProductId.Structura))
                .WaitAsync(TimeSpan.FromSeconds(5));
            await host.RecoverLocalAsync();

            Assert.Equal(new[] { "queued-before-drain", "submitted-after-resume" }, executor.RequestIds);
            Assert.Empty(await new FileRequestIdempotencyStore(statePath)
                .GetLocalJobsAsync(CancellationToken.None));
        }
        finally
        {
            DeleteRuntimeRoot(root);
        }
    }

    [Fact]
    public async Task CorruptDurableState_FailsClosedBeforeExecutorStarts()
    {
        var root = CreateRuntimeRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "idempotency-state.json"),
                "{\"schemaVersion\":2,\"localJobs\":");
            var executor = new RecordingExecutor();
            await using var host = new ConnectorRuntimeHost(
                [],
                [executor],
                runtimeRootDirectory: root,
                localDeviceId: "device-a");

            await Assert.ThrowsAsync<InvalidDataException>(
                () => host.SubmitLocalAsync(BuildJob("must-not-run", ConnectorProductId.Structura)));
            Assert.Empty(executor.RequestIds);
        }
        finally
        {
            DeleteRuntimeRoot(root);
        }
    }

    [Fact]
    public async Task CompletedDuplicate_ReturnsSameTerminalWithoutReexecution()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var job = BuildJob("stable-request", ConnectorProductId.Platform);
            var firstExecutor = new RecordingExecutor();
            ConnectorJobStatusEnvelope firstTerminal;
            await using (var firstHost = new ConnectorRuntimeHost(
                             [],
                             [firstExecutor],
                             runtimeRootDirectory: root,
                             localDeviceId: "device-a"))
            {
                firstTerminal = await firstHost.SubmitLocalAsync(job).WaitAsync(TimeSpan.FromSeconds(5));
            }

            var secondExecutor = new RecordingExecutor();
            await using (var secondHost = new ConnectorRuntimeHost(
                             [],
                             [secondExecutor],
                             runtimeRootDirectory: root,
                             localDeviceId: "device-a"))
            {
                var prepared = await secondHost.PrepareLocalAsync();
                var history = Assert.Single(prepared.Jobs);
                AssertJobEquivalent(job, history.Job);
                AssertStatusEquivalent(firstTerminal, history.LastStatus);
                Assert.Null(history.PersistedState);
                var duplicate = await secondHost.SubmitLocalAsync(job).WaitAsync(TimeSpan.FromSeconds(5));
                AssertStatusEquivalent(firstTerminal, duplicate);
            }

            Assert.Equal(new[] { "stable-request" }, firstExecutor.RequestIds);
            Assert.Empty(secondExecutor.RequestIds);
        }
        finally
        {
            DeleteRuntimeRoot(root);
        }
    }

    private static ConnectorJobEnvelope BuildJob(string requestId, ConnectorProductId productId)
        => new(
            SchemaVersion: 2,
            RequestId: requestId,
            ModuleId: "converters",
            Provider: CadProvider.AutoCad,
            Operation: JobOperation.Export,
            CreatedAtUtc: DateTime.UtcNow,
            Payload: JsonSerializer.SerializeToElement(new { inputPath = $"C:/input/{requestId}.ifc" }),
            CorrelationId: $"corr-{requestId}",
            ExecutorId: RecordingExecutor.Id,
            Scope: ExecutionScope.DeviceLocal(productId));

    private static void AssertJobEquivalent(ConnectorJobEnvelope expected, ConnectorJobEnvelope actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.ModuleId, actual.ModuleId);
        Assert.Equal(expected.Provider, actual.Provider);
        Assert.Equal(expected.Operation, actual.Operation);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(expected.CorrelationId, actual.CorrelationId);
        Assert.Equal(expected.EffectiveExecutorId, actual.EffectiveExecutorId);
        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.Payload.GetRawText(), actual.Payload.GetRawText());
    }

    private static void AssertStatusEquivalent(
        ConnectorJobStatusEnvelope expected,
        ConnectorJobStatusEnvelope actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.DeviceId, actual.DeviceId);
        Assert.Equal(expected.ModuleId, actual.ModuleId);
        Assert.Equal(expected.Provider, actual.Provider);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.UpdatedAtUtc, actual.UpdatedAtUtc);
        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(expected.Progress, actual.Progress);
        Assert.Equal(expected.ErrorCode, actual.ErrorCode);
        Assert.Equal(expected.CorrelationId, actual.CorrelationId);
        Assert.Equal(expected.ExecutorId, actual.ExecutorId);
        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.Result?.GetRawText(), actual.Result?.GetRawText());
    }

    private static string CreateRuntimeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"connector-local-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRuntimeRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingExecutor : IConnectorJobExecutor
    {
        public const string Id = "test.local-recovery";
        private readonly ConcurrentQueue<string> _requestIds = new();
        public string ExecutorId => Id;
        public IReadOnlyList<string> RequestIds => _requestIds.ToArray();

        public Task<ConnectorExecutionResult> ExecuteAsync(
            ConnectorJobEnvelope job,
            Func<ConnectorExecutionProgress, CancellationToken, Task>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requestIds.Enqueue(job.RequestId);
            var result = JsonSerializer.SerializeToElement(new
            {
                artifactReceipt = new
                {
                    artifactId = $"artifact-{job.RequestId}",
                    relativePath = $"results/{job.RequestId}.glb"
                }
            });
            return Task.FromResult(new ConnectorExecutionResult(true, "done", Result: result));
        }
    }
}
