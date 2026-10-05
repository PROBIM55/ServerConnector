using System.Collections.Concurrent;
using System.Text.Json;
using Platform.Connector.Core;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class MixedAgentRecoveryIntegrationTests
{
    [Fact]
    public async Task ControlledShutdown_InterruptsRunningStructuraIfc_AndRecoversMixedQueuedJobsInFifo()
    {
        var root = Path.Combine(Path.GetTempPath(), $"connector-mixed-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var statePath = Path.Combine(root, "idempotency-state.json");
            var runningStructuraIfc = BuildJob(
                "structura-running-ifc",
                ConnectorProductId.Structura,
                "model.ifc");
            var recoveredPlatformFbx = BuildJob(
                "platform-recovered-fbx",
                ConnectorProductId.Platform,
                "scene.fbx");
            var recoveredStructuraIfc = BuildJob(
                "structura-recovered-ifc",
                ConnectorProductId.Structura,
                "queued.ifc");
            var submittedAfterRestart = BuildJob(
                "platform-after-restart-fbx",
                ConnectorProductId.Platform,
                "after-restart.fbx");

            var blockingExecutor = new BlockingExecutor();
            var statuses = new ConcurrentQueue<ConnectorJobStatusEnvelope>();
            Task<ConnectorJobStatusEnvelope> runningCompletion;
            Task<ConnectorJobStatusEnvelope> firstQueuedCompletion;
            Task<ConnectorJobStatusEnvelope> secondQueuedCompletion;
            await using (var firstHost = new ConnectorRuntimeHost(
                             [],
                             [blockingExecutor],
                             runtimeRootDirectory: root,
                             localDeviceId: "device-mixed"))
            {
                firstHost.JobStatusChanged += statuses.Enqueue;
                runningCompletion = firstHost.SubmitLocalAsync(runningStructuraIfc);
                await blockingExecutor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

                firstQueuedCompletion = firstHost.SubmitLocalAsync(recoveredPlatformFbx);
                secondQueuedCompletion = firstHost.SubmitLocalAsync(recoveredStructuraIfc);
                var beforeShutdown = new FileRequestIdempotencyStore(statePath);
                var persistedBeforeShutdown = await beforeShutdown.GetLocalJobsAsync(CancellationToken.None);
                Assert.Equal(
                    new[]
                    {
                        runningStructuraIfc.RequestId,
                        recoveredPlatformFbx.RequestId,
                        recoveredStructuraIfc.RequestId
                    },
                    persistedBeforeShutdown.Select(item => item.Job.RequestId));
                Assert.Equal(StoredLocalJobState.Running, persistedBeforeShutdown[0].State);
                Assert.All(persistedBeforeShutdown.Skip(1), item => Assert.Equal(StoredLocalJobState.Queued, item.State));

                await firstHost.DisposeAsync();
            }

            var stoppedRunningTerminal = await runningCompletion;
            Assert.Equal(JobStatus.Interrupted, stoppedRunningTerminal.Status);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstQueuedCompletion);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondQueuedCompletion);
            Assert.Equal(new[] { runningStructuraIfc.RequestId }, blockingExecutor.RequestIds);

            var executor = new RecordingExecutor();
            await using (var restartedHost = new ConnectorRuntimeHost(
                             [],
                             [executor],
                             runtimeRootDirectory: root,
                             localDeviceId: "device-mixed"))
            {
                restartedHost.JobStatusChanged += statuses.Enqueue;
                var prepared = await restartedHost.PrepareLocalAsync();

                Assert.Equal(
                    new[]
                    {
                        runningStructuraIfc.RequestId,
                        recoveredPlatformFbx.RequestId,
                        recoveredStructuraIfc.RequestId
                    },
                    prepared.Jobs.Select(item => item.Job.RequestId));
                Assert.Equal(JobStatus.Interrupted, prepared.Jobs[0].LastStatus.Status);
                Assert.Null(prepared.Jobs[0].PersistedState);
                Assert.Equal(ConnectorProductId.Structura, prepared.Jobs[0].Job.Scope!.ProductId);
                Assert.Equal(JobStatus.Queued, prepared.Jobs[1].LastStatus.Status);
                Assert.Equal(StoredLocalJobState.Queued, prepared.Jobs[1].PersistedState);
                Assert.Equal(ConnectorProductId.Platform, prepared.Jobs[1].Job.Scope!.ProductId);
                Assert.Equal(JobStatus.Queued, prepared.Jobs[2].LastStatus.Status);
                Assert.Equal(StoredLocalJobState.Queued, prepared.Jobs[2].PersistedState);
                Assert.Equal(ConnectorProductId.Structura, prepared.Jobs[2].Job.Scope!.ProductId);

                await restartedHost.RecoverLocalAsync();
                var terminal = await restartedHost.SubmitLocalAsync(submittedAfterRestart)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(JobStatus.Success, terminal.Status);
            }

            Assert.Equal(
                new[]
                {
                    recoveredPlatformFbx.RequestId,
                    recoveredStructuraIfc.RequestId,
                    submittedAfterRestart.RequestId
                },
                executor.RequestIds);
            var interrupted = Assert.Single(
                statuses,
                status => status.RequestId == runningStructuraIfc.RequestId && status.Status == JobStatus.Interrupted);
            Assert.Equal(JobStatus.Interrupted, interrupted.Status);
            Assert.Equal("CONNECTOR_EXECUTION_INTERRUPTED_REVIEW_REQUIRED", interrupted.ErrorCode);

            var reloaded = new FileRequestIdempotencyStore(statePath);
            Assert.Empty(await reloaded.GetLocalJobsAsync(CancellationToken.None));
            await AssertTerminalAsync(reloaded, runningStructuraIfc, JobStatus.Interrupted, artifactId: null);
            await AssertTerminalAsync(reloaded, recoveredPlatformFbx, JobStatus.Success, "artifact-platform-recovered-fbx");
            await AssertTerminalAsync(reloaded, recoveredStructuraIfc, JobStatus.Success, "artifact-structura-recovered-ifc");
            await AssertTerminalAsync(reloaded, submittedAfterRestart, JobStatus.Success, "artifact-platform-after-restart-fbx");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ConnectorJobEnvelope BuildJob(string requestId, ConnectorProductId productId, string inputFile)
        => new(
            SchemaVersion: 2,
            RequestId: requestId,
            ModuleId: "converters",
            Provider: CadProvider.AutoCad,
            Operation: JobOperation.Export,
            CreatedAtUtc: DateTime.UtcNow,
            Payload: JsonSerializer.SerializeToElement(new { inputPath = $"C:/fixture/{inputFile}" }),
            CorrelationId: $"corr-{requestId}",
            ExecutorId: RecordingExecutor.Id,
            Scope: ExecutionScope.DeviceLocal(productId));

    private static async Task AssertTerminalAsync(
        FileRequestIdempotencyStore store,
        ConnectorJobEnvelope expectedJob,
        JobStatus expectedStatus,
        string? artifactId)
    {
        var stored = await store.TryGetAsync(expectedJob.RequestId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(expectedStatus, stored!.Envelope.Status);
        Assert.Equal(expectedJob.Scope, stored.Envelope.Scope);
        Assert.Equal(expectedJob.Scope, stored.SourceJob!.Scope);
        Assert.Equal(expectedJob.Payload.GetRawText(), stored.SourceJob.Payload.GetRawText());
        if (artifactId is null)
        {
            Assert.Null(stored.Envelope.Result);
            return;
        }

        Assert.Equal(
            artifactId,
            stored.Envelope.Result!.Value
                .GetProperty("artifactReceipt")
                .GetProperty("artifactId")
                .GetString());
    }

    private sealed class RecordingExecutor : IConnectorJobExecutor
    {
        public const string Id = "test.mixed-agent-recovery";
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

    private sealed class BlockingExecutor : IConnectorJobExecutor
    {
        public string ExecutorId => RecordingExecutor.Id;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _requestIds = new();

        public IReadOnlyList<string> RequestIds => _requestIds.ToArray();

        public async Task<ConnectorExecutionResult> ExecuteAsync(
            ConnectorJobEnvelope job,
            Func<ConnectorExecutionProgress, CancellationToken, Task>? progress,
            CancellationToken cancellationToken)
        {
            _requestIds.Enqueue(job.RequestId);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ConnectorExecutionResult(false);
        }
    }
}
