using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class ConnectorRuntimeSafetyTests
{
    [Fact]
    public async Task RestartAsync_RefusesToReopenAnyDrainPhase()
    {
        await using var host = new ConnectorRuntimeHost([], runtimeRootDirectory: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Equal(ConnectorDrainPhase.ReadyToApply, (await host.RequestDrainAsync(TimeSpan.Zero)).Phase);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.RestartAsync());

        Assert.Contains("Resume", error.Message);
        Assert.Equal(ConnectorDrainPhase.ReadyToApply, host.DrainSnapshot.Phase);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task HttpStatusPayload_ProjectsLegacyV1AndPreservesV2Scope(int schemaVersion, bool expectExtendedFields)
    {
        var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new HttpConnectorControlPlaneClient(httpClient);
        var status = new ConnectorJobStatusEnvelope(
            schemaVersion,
            "request-1",
            "device-1",
            "module-1",
            CadProvider.AutoCad,
            JobStatus.Success,
            DateTime.UtcNow,
            ExecutorId: "converter.fbx-glb",
            Scope: ExecutionScope.Project(ConnectorProductId.Platform, "tenant", "project"));

        await client.SendJobStatusAsync(
            new ConnectorRuntimeOptions { ServerUrl = "https://connector.test", DeviceToken = "token" },
            status,
            CancellationToken.None);

        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        Assert.Equal("autocad", root.GetProperty("provider").GetString());
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal(expectExtendedFields, root.TryGetProperty("executorId", out _));
        Assert.Equal(expectExtendedFields, root.TryGetProperty("scope", out _));
    }

    [Fact]
    public async Task TerminalPublishFailure_HoldsDrainUntilExplicitOnlineStop()
    {
        var gate = new ConnectorExecutionGate();
        var store = new TestStore();
        var publisher = new FailingTerminalPublisher();
        var logger = new SignalLogger();
        var executor = new SuccessfulExecutor();
        var runner = new ConnectorJobRunner([], publisher, store, logger, [executor]);
        await using var dispatcher = new ConnectorJobDispatcher(runner, gate, publisher);
        var job = new ConnectorJobEnvelope(
            2,
            "publish-failure",
            "module-1",
            CadProvider.AutoCad,
            JobOperation.Export,
            DateTime.UtcNow,
            JsonSerializer.SerializeToElement(new { }),
            ExecutorId: executor.ExecutorId,
            Scope: ExecutionScope.Project(ConnectorProductId.Platform, "tenant", "project"));
        var controlPlane = new RepeatJobControlPlane(job);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var worker = new ConnectorWorker(controlPlane, runner, publisher, store, logger, gate, dispatcher);
        var running = worker.RunAsync(new ConnectorRuntimeOptions
        {
            DeviceId = "device-1",
            DeviceToken = "token",
            PollIntervalSeconds = 1,
            PollBackoffMaxSeconds = 2,
            HeartbeatSeconds = 30
        }, stop.Token);

        await publisher.RetryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var drain = gate.RequestDrainAsync(TimeSpan.FromSeconds(5));

        Assert.False(drain.IsCompleted);
        Assert.Equal(1, gate.Snapshot.ActiveOperations);
        Assert.True(publisher.TerminalAttempts >= 4);
        Assert.Equal(1, executor.Calls);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConnectorDrainPhase.ReadyToApply, (await drain).Phase);
        Assert.False((await store.TryGetAsync(job.RequestId, CancellationToken.None))!.Published);
    }

    [Fact]
    public async Task NewHost_ReplaysDurableTerminalAfterStopWithoutJobRedelivery()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-outbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var job = new ConnectorJobEnvelope(
                2,
                "durable-replay",
                "module-1",
                CadProvider.AutoCad,
                JobOperation.Export,
                DateTime.UtcNow,
                JsonSerializer.SerializeToElement(new { }),
                ExecutorId: "test.publish",
                Scope: ExecutionScope.Project(ConnectorProductId.Platform, "tenant", "project"));
            var firstExecutor = new SuccessfulExecutor();
            var failingServer = new RuntimeServerHandler(job, failTerminal: true, blockTerminal: true);
            await using (var firstHost = new ConnectorRuntimeHost(
                             [],
                             [firstExecutor],
                             runtimeRootDirectory: root,
                             httpMessageHandler: failingServer))
            {
                await firstHost.StartAsync(BuildRuntimeOptions());
                await failingServer.TerminalRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await firstHost.StopAsync();
            }

            var persisted = new FileRequestIdempotencyStore(Path.Combine(root, "idempotency-state.json"));
            var pending = (await persisted.TryGetAsync(job.RequestId, CancellationToken.None))!;
            Assert.False(pending.Published);
            Assert.Equal(JobStatus.Success, pending.Envelope.Status);
            Assert.Equal(ConnectorScopeKind.Project, pending.Envelope.Scope!.ScopeKind);
            Assert.Equal("https://connector.test", pending.RemoteAuthority);
            using (var persistedJson = JsonDocument.Parse(
                       await File.ReadAllTextAsync(Path.Combine(root, "idempotency-state.json"))))
            {
                var record = persistedJson.RootElement.GetProperty("terminals").GetProperty(job.RequestId);
                Assert.Equal("https://connector.test", record.GetProperty("remoteAuthority").GetString());
                Assert.Equal(
                    "project",
                    record.GetProperty("envelope").GetProperty("scope").GetProperty("scopeKind").GetString());
            }
            Assert.Equal(1, firstExecutor.Calls);

            var secondExecutor = new SuccessfulExecutor();
            var recoveringServer = new RuntimeServerHandler(job: null, failTerminal: false, blockTerminal: true);
            await using (var secondHost = new ConnectorRuntimeHost(
                             [],
                             [secondExecutor],
                             runtimeRootDirectory: root,
                             httpMessageHandler: recoveringServer))
            {
                await secondHost.StartAsync(BuildRuntimeOptions());
                await recoveringServer.TerminalRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var drain = secondHost.RequestDrainAsync(TimeSpan.FromSeconds(5));

                Assert.False(drain.IsCompleted);
                Assert.Equal(1, secondHost.DrainSnapshot.ActiveOperations);
                recoveringServer.AllowTerminalResponse.TrySetResult();
                Assert.Equal(ConnectorDrainPhase.ReadyToApply, (await drain).Phase);
                Assert.Equal(0, secondExecutor.Calls);
                Assert.Equal(0, recoveringServer.DeliveredJobs);

                var reloaded = new FileRequestIdempotencyStore(Path.Combine(root, "idempotency-state.json"));
                Assert.True((await reloaded.TryGetAsync(job.RequestId, CancellationToken.None))!.Published);
                await secondHost.StopAsync();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DurableOutbox_SkipsWrongAuthorityLegacyAndDeviceLocalRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-outbox-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var statePath = Path.Combine(root, "idempotency-state.json");
            var store = new FileRequestIdempotencyStore(statePath);
            var projectScope = ExecutionScope.Project(ConnectorProductId.Platform, "tenant", "project");
            await store.SaveTerminalAsync(
                BuildStoredTerminal("wrong-server", projectScope),
                published: false,
                CancellationToken.None,
                ConnectorRemoteAuthority.Normalize("https://user:secret@SERVER-A.test:443/api/"));
            await store.SaveTerminalAsync(
                BuildStoredTerminal("legacy-no-authority", projectScope),
                published: false,
                CancellationToken.None);
            await store.SaveTerminalAsync(
                BuildStoredTerminal(
                    "device-local",
                    ExecutionScope.DeviceLocal(ConnectorProductId.Structura)),
                published: false,
                CancellationToken.None);

            var server = new RuntimeServerHandler(job: null, failTerminal: false, blockTerminal: false);
            await using var host = new ConnectorRuntimeHost(
                [],
                [new SuccessfulExecutor()],
                runtimeRootDirectory: root,
                httpMessageHandler: server);

            await host.StartAsync(BuildRuntimeOptions("https://server-b.test/api"));
            await server.JobPollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, server.StatusPosts);
            Assert.False(server.TerminalRequestStarted.Task.IsCompleted);
            Assert.All(
                await new FileRequestIdempotencyStore(statePath).GetPendingAsync(CancellationToken.None),
                item => Assert.False(item.Published));
            await host.StopAsync();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ConnectorRuntimeOptions BuildRuntimeOptions(string serverUrl = "https://connector.test") => new()
    {
        ServerUrl = serverUrl,
        DeviceId = "device-1",
        DeviceToken = "token",
        PollIntervalSeconds = 1,
        PollBackoffMaxSeconds = 2,
        HeartbeatSeconds = 30
    };

    private static ConnectorJobStatusEnvelope BuildStoredTerminal(string requestId, ExecutionScope scope)
        => new(
            SchemaVersion: 2,
            RequestId: requestId,
            DeviceId: "device-1",
            ModuleId: "module-1",
            Provider: CadProvider.AutoCad,
            Status: JobStatus.Success,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: "done",
            Progress: 100,
            ExecutorId: "test.publish",
            Scope: scope);

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RuntimeServerHandler(
        ConnectorJobEnvelope? job,
        bool failTerminal,
        bool blockTerminal) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions WireOptions = CreateWireOptions();
        private int _jobDelivered;
        private int _statusPosts;

        public int DeliveredJobs => Volatile.Read(ref _jobDelivered);
        public int StatusPosts => Volatile.Read(ref _statusPosts);
        public TaskCompletionSource TerminalRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowTerminalResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource JobPollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/health", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, "{}");
            }
            if (path.EndsWith("/connect/bootstrap", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, "{\"ok\":true,\"session_id\":\"session-1\",\"device_id\":\"device-1\",\"heartbeat_seconds\":30}");
            }
            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, "{}");
            }
            if (path.EndsWith("/connector/jobs/next", StringComparison.Ordinal))
            {
                JobPollStarted.TrySetResult();
                if (job is not null && Interlocked.Exchange(ref _jobDelivered, 1) == 0)
                {
                    return Json(HttpStatusCode.OK, JsonSerializer.Serialize(job, WireOptions));
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/status", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _statusPosts);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var payload = JsonDocument.Parse(body);
                if (payload.RootElement.GetProperty("status").GetString() == "success")
                {
                    TerminalRequestStarted.TrySetResult();
                    if (blockTerminal)
                    {
                        await AllowTerminalResponse.Task.WaitAsync(cancellationToken);
                    }
                    return Json(failTerminal ? HttpStatusCode.InternalServerError : HttpStatusCode.OK, "{}");
                }
                return Json(HttpStatusCode.OK, "{}");
            }

            throw new InvalidOperationException($"Unexpected connector request: {request.Method} {path}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        private static JsonSerializerOptions CreateWireOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return options;
        }
    }

    private sealed class SuccessfulExecutor : IConnectorJobExecutor
    {
        public string ExecutorId => "test.publish";
        public int Calls { get; private set; }

        public Task<ConnectorExecutionResult> ExecuteAsync(
            ConnectorJobEnvelope job,
            Func<ConnectorExecutionProgress, CancellationToken, Task>? progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ConnectorExecutionResult(true, "done"));
        }
    }

    private sealed class FailingTerminalPublisher : IJobStatusPublisher
    {
        public int TerminalAttempts { get; private set; }
        public TaskCompletionSource RetryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
        {
            if (status.Status == JobStatus.Success)
            {
                TerminalAttempts++;
                if (TerminalAttempts == 4)
                {
                    RetryStarted.TrySetResult();
                }
                throw new HttpRequestException("status endpoint unavailable");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class SignalLogger : IConnectorLogger
    {
        public void Info(string message, ConnectorJobEnvelope? job = null, string? requestId = null) { }
        public void Warn(string message, ConnectorJobEnvelope? job = null, string? requestId = null, Exception? exception = null) { }
        public void Error(string message, ConnectorJobEnvelope? job = null, string? requestId = null, Exception? exception = null) { }
    }

    private sealed class RepeatJobControlPlane(ConnectorJobEnvelope job) : IConnectorControlPlaneClient
    {
        public Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => Task.FromResult(new BootstrapResponseDto { Ok = true });
        public Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendJobStatusAsync(ConnectorRuntimeOptions options, ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => Task.FromResult<ConnectorJobEnvelope?>(job);
    }

    private sealed class TestStore : IRequestIdempotencyStore
    {
        private StoredTerminalStatus? _stored;
        public Task<StoredTerminalStatus?> TryGetAsync(string requestId, CancellationToken cancellationToken)
            => Task.FromResult(_stored?.Envelope.RequestId == requestId ? _stored : null);
        public Task SaveTerminalAsync(ConnectorJobStatusEnvelope envelope, bool published, CancellationToken cancellationToken)
        {
            _stored = new StoredTerminalStatus(envelope, published, DateTime.UtcNow);
            return Task.CompletedTask;
        }
        public Task MarkPublishedAsync(string requestId, CancellationToken cancellationToken)
        {
            if (_stored is not null)
            {
                _stored = _stored with { Published = true };
            }
            return Task.CompletedTask;
        }
    }
}
