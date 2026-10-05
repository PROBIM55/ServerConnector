using System.Net;
using System.Text.Json;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class IdempotencyAndWorkerTests
{
    [Fact]
    public async Task FileStore_WhenTerminalIsPublished_DropsHeavyResultAndReloadsState()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"connector-idempotency-{Guid.NewGuid():N}.json");
        try
        {
            var result = JsonSerializer.SerializeToElement(new { mesh = new string('x', 200_000) });
            var status = BuildTerminalStatus("req-heavy", result);
            var store = new FileRequestIdempotencyStore(statePath, maxPublishedEntries: 8);

            await store.SaveTerminalAsync(status, published: false, CancellationToken.None);
            var unpublishedSize = new FileInfo(statePath).Length;
            await store.MarkPublishedAsync(status.RequestId, CancellationToken.None);

            var reloaded = new FileRequestIdempotencyStore(statePath, maxPublishedEntries: 8);
            var saved = await reloaded.TryGetAsync(status.RequestId, CancellationToken.None);
            Assert.NotNull(saved);
            Assert.True(saved!.Published);
            Assert.Null(saved.Envelope.Result);
            Assert.True(new FileInfo(statePath).Length < unpublishedSize / 4);
        }
        finally
        {
            File.Delete(statePath);
            File.Delete(statePath + ".tmp");
        }
    }

    [Fact]
    public async Task FileStore_PrunesOnlyPublishedHistory()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"connector-idempotency-{Guid.NewGuid():N}.json");
        try
        {
            var store = new FileRequestIdempotencyStore(statePath, maxPublishedEntries: 2);
            foreach (var requestId in new[] { "published-1", "published-2", "published-3" })
            {
                await store.SaveTerminalAsync(BuildTerminalStatus(requestId), published: false, CancellationToken.None);
                await store.MarkPublishedAsync(requestId, CancellationToken.None);
            }
            await store.SaveTerminalAsync(BuildTerminalStatus("unpublished"), published: false, CancellationToken.None);

            var reloaded = new FileRequestIdempotencyStore(statePath, maxPublishedEntries: 2);
            Assert.Null(await reloaded.TryGetAsync("published-1", CancellationToken.None));
            Assert.NotNull(await reloaded.TryGetAsync("published-2", CancellationToken.None));
            Assert.NotNull(await reloaded.TryGetAsync("published-3", CancellationToken.None));
            var unpublished = await reloaded.TryGetAsync("unpublished", CancellationToken.None);
            Assert.NotNull(unpublished);
            Assert.False(unpublished!.Published);
        }
        finally
        {
            File.Delete(statePath);
            File.Delete(statePath + ".tmp");
        }
    }

    [Fact]
    public async Task RunOnceAsync_WhenTerminalPublishFails_PersistsUnpublishedTerminal()
    {
        var store = new InMemoryIdempotencyStore();
        var publisher = new FailingOnSuccessPublisher();
        var runner = new ConnectorJobRunner(
            new[] { new FakeProvider(CadProvider.AutoCad, success: true) },
            publisher,
            store,
            new JsonLineConnectorLogger(null));

        await Assert.ThrowsAnyAsync<Exception>(() => runner.RunOnceAsync("device-1", BuildJob("req-persist", CadProvider.AutoCad)));

        var saved = await store.TryGetAsync("req-persist", CancellationToken.None);
        Assert.NotNull(saved);
        Assert.False(saved!.Published);
        Assert.Equal(JobStatus.Success, saved.Envelope.Status);
    }

    private static ConnectorJobStatusEnvelope BuildTerminalStatus(string requestId, JsonElement? result = null)
        => new(
            SchemaVersion: 1,
            RequestId: requestId,
            DeviceId: "device-1",
            ModuleId: "bridge",
            Provider: CadProvider.Tekla,
            Status: JobStatus.Success,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: "done",
            Result: result);

    [Fact]
    public async Task Worker_WhenJobAlreadySavedAsUnpublished_RePublishesWithoutExecutingProvider()
    {
        var requestId = "req-dup-1";
        var store = new InMemoryIdempotencyStore();
        var status = new ConnectorJobStatusEnvelope(
            SchemaVersion: 1,
            RequestId: requestId,
            DeviceId: "device-1",
            ModuleId: "shze",
            Provider: CadProvider.AutoCad,
            Status: JobStatus.Success,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: "done");
        await store.SaveTerminalAsync(status, published: false, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var controlPlane = new SingleJobControlPlane(BuildJob(requestId, CadProvider.AutoCad));
        var publisher = new RecordingPublisher(() => cts.Cancel());
        var runner = new ConnectorJobRunner(
            new[] { new FakeProvider(CadProvider.AutoCad, success: true) },
            publisher,
            store,
            new JsonLineConnectorLogger(null));
        var worker = new ConnectorWorker(
            controlPlane,
            runner,
            publisher,
            store,
            new JsonLineConnectorLogger(null));

        await worker.RunAsync(new ConnectorRuntimeOptions
        {
            DeviceId = "device-1",
            DeviceToken = "token",
            EnableJobPolling = true,
            PollIntervalSeconds = 1,
            PollBackoffMaxSeconds = 2,
            HeartbeatSeconds = 30
        }, cts.Token);

        var republished = await store.TryGetAsync(requestId, CancellationToken.None);
        Assert.NotNull(republished);
        Assert.True(republished!.Published);
        Assert.Single(publisher.Messages);
        Assert.Equal(JobStatus.Success, publisher.Messages[0].Status);
    }

    [Fact]
    public async Task Worker_WhenPollFailsRepeatedly_DoesNotCrashAndStopsOnCancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var controlPlane = new AlwaysFailPollControlPlane();
        var publisher = new RecordingPublisher(() => { });
        var store = new InMemoryIdempotencyStore();
        var runner = new ConnectorJobRunner(
            new[] { new FakeProvider(CadProvider.AutoCad, success: true) },
            publisher,
            store,
            new JsonLineConnectorLogger(null));
        var worker = new ConnectorWorker(
            controlPlane,
            runner,
            publisher,
            store,
            new JsonLineConnectorLogger(null));

        await worker.RunAsync(new ConnectorRuntimeOptions
        {
            DeviceId = "device-1",
            DeviceToken = "token",
            EnableJobPolling = true,
            PollIntervalSeconds = 1,
            PollBackoffMaxSeconds = 2,
            HeartbeatSeconds = 30
        }, cts.Token);

        Assert.Empty(publisher.Messages);
    }

    [Fact]
    public async Task Worker_WhenHeartbeatReturnsSessionConflict_StopsAndDoesNotPoll()
    {
        var controlPlane = new HeartbeatSessionConflictControlPlane();
        var publisher = new RecordingPublisher(() => { });
        var store = new InMemoryIdempotencyStore();
        var runner = new ConnectorJobRunner(
            new[] { new FakeProvider(CadProvider.AutoCad, success: true) },
            publisher,
            store,
            new JsonLineConnectorLogger(null));
        var worker = new ConnectorWorker(
            controlPlane,
            runner,
            publisher,
            store,
            new JsonLineConnectorLogger(null));

        var reason = await worker.RunAsync(new ConnectorRuntimeOptions
        {
            DeviceId = "device-1",
            DeviceToken = "token",
            EnableJobPolling = true,
            PollIntervalSeconds = 1,
            PollBackoffMaxSeconds = 2,
            HeartbeatSeconds = 30
        }, CancellationToken.None);

        Assert.Equal(ConnectorWorkerExitReason.SessionConflict, reason);
        Assert.Equal(1, controlPlane.HeartbeatCalls);
        Assert.Equal(0, controlPlane.PollCalls);
    }

    [Fact]
    public async Task Worker_WhenPollReturnsSessionConflict_StopsLoop()
    {
        var controlPlane = new PollSessionConflictControlPlane();
        var publisher = new RecordingPublisher(() => { });
        var store = new InMemoryIdempotencyStore();
        var runner = new ConnectorJobRunner(
            new[] { new FakeProvider(CadProvider.AutoCad, success: true) },
            publisher,
            store,
            new JsonLineConnectorLogger(null));
        var worker = new ConnectorWorker(
            controlPlane,
            runner,
            publisher,
            store,
            new JsonLineConnectorLogger(null));

        var reason = await worker.RunAsync(new ConnectorRuntimeOptions
        {
            DeviceId = "device-1",
            DeviceToken = "token",
            EnableJobPolling = true,
            PollIntervalSeconds = 1,
            PollBackoffMaxSeconds = 2,
            HeartbeatSeconds = 30
        }, CancellationToken.None);

        Assert.Equal(ConnectorWorkerExitReason.SessionConflict, reason);
        Assert.Equal(1, controlPlane.HeartbeatCalls);
        Assert.Equal(1, controlPlane.PollCalls);
    }

    [Fact]
    public async Task Worker_WhenHeartbeatReturnsTokenRejected_StopsWithTokenRejectedReason()
    {
        var controlPlane = new HeartbeatTokenRejectedControlPlane();
        var publisher = new RecordingPublisher(() => { });
        var store = new InMemoryIdempotencyStore();
        var runner = new ConnectorJobRunner(
            new[] { new FakeProvider(CadProvider.AutoCad, success: true) },
            publisher,
            store,
            new JsonLineConnectorLogger(null, writeToConsole: false));
        var worker = new ConnectorWorker(
            controlPlane,
            runner,
            publisher,
            store,
            new JsonLineConnectorLogger(null, writeToConsole: false));

        var reason = await worker.RunAsync(new ConnectorRuntimeOptions
        {
            DeviceId = "device-1",
            DeviceToken = "token",
            EnableJobPolling = true,
            PollIntervalSeconds = 1,
            PollBackoffMaxSeconds = 2,
            HeartbeatSeconds = 30
        }, CancellationToken.None);

        Assert.Equal(ConnectorWorkerExitReason.TokenRejected, reason);
        Assert.Equal(0, controlPlane.PollCalls);
    }

    private static ConnectorJobEnvelope BuildJob(string requestId, CadProvider provider)
    {
        using var payload = JsonDocument.Parse("""{"value":1}""");
        return new ConnectorJobEnvelope(
            SchemaVersion: 1,
            RequestId: requestId,
            ModuleId: "shze",
            Provider: provider,
            Operation: JobOperation.Build,
            CreatedAtUtc: DateTime.UtcNow,
            Payload: payload.RootElement.Clone(),
            CorrelationId: $"corr-{requestId}");
    }

    private sealed class SingleJobControlPlane(ConnectorJobEnvelope job) : IConnectorControlPlaneClient
    {
        private int _pollCount;

        public Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => Task.FromResult(new BootstrapResponseDto { Ok = true, DeviceId = options.DeviceId, SessionId = "s1", HeartbeatSeconds = 30 });
        public Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendJobStatusAsync(ConnectorRuntimeOptions options, ConnectorJobStatusEnvelope status, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            var value = Interlocked.Increment(ref _pollCount) == 1 ? job : null;
            return Task.FromResult(value);
        }
    }

    private sealed class AlwaysFailPollControlPlane : IConnectorControlPlaneClient
    {
        public Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => Task.FromResult(new BootstrapResponseDto { Ok = true, DeviceId = options.DeviceId, SessionId = "s1", HeartbeatSeconds = 30 });
        public Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendJobStatusAsync(ConnectorRuntimeOptions options, ConnectorJobStatusEnvelope status, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }

    private sealed class HeartbeatSessionConflictControlPlane : IConnectorControlPlaneClient
    {
        public int HeartbeatCalls { get; private set; }
        public int PollCalls { get; private set; }

        public Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => Task.FromResult(new BootstrapResponseDto { Ok = true, DeviceId = options.DeviceId, SessionId = "s1", HeartbeatSeconds = 30 });

        public Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            HeartbeatCalls++;
            throw new SessionConflictException("Session superseded by newer login.", HttpStatusCode.Conflict);
        }

        public Task SendJobStatusAsync(ConnectorRuntimeOptions options, ConnectorJobStatusEnvelope status, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            PollCalls++;
            return Task.FromResult<ConnectorJobEnvelope?>(null);
        }
    }

    private sealed class HeartbeatTokenRejectedControlPlane : IConnectorControlPlaneClient
    {
        public int PollCalls { get; private set; }

        public Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => Task.FromResult(new BootstrapResponseDto { Ok = true, DeviceId = options.DeviceId, SessionId = "s1", HeartbeatSeconds = 30 });

        public Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => throw new TokenRejectedException("Token revoked.", HttpStatusCode.Unauthorized);

        public Task SendJobStatusAsync(ConnectorRuntimeOptions options, ConnectorJobStatusEnvelope status, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            PollCalls++;
            return Task.FromResult<ConnectorJobEnvelope?>(null);
        }
    }

    private sealed class PollSessionConflictControlPlane : IConnectorControlPlaneClient
    {
        public int HeartbeatCalls { get; private set; }
        public int PollCalls { get; private set; }

        public Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => Task.FromResult(new BootstrapResponseDto { Ok = true, DeviceId = options.DeviceId, SessionId = "s1", HeartbeatSeconds = 30 });

        public Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            HeartbeatCalls++;
            return Task.CompletedTask;
        }

        public Task SendJobStatusAsync(ConnectorRuntimeOptions options, ConnectorJobStatusEnvelope status, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            PollCalls++;
            throw new SessionConflictException("Session superseded by newer login.", HttpStatusCode.Conflict);
        }
    }

    private sealed class InMemoryIdempotencyStore : IRequestIdempotencyStore
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

    private sealed class RecordingPublisher(Action onPublish) : IJobStatusPublisher
    {
        public List<ConnectorJobStatusEnvelope> Messages { get; } = new();

        public Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
        {
            Messages.Add(status);
            onPublish();
            return Task.CompletedTask;
        }
    }

    private sealed class FailingOnSuccessPublisher : IJobStatusPublisher
    {
        public Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
        {
            if (status.Status == JobStatus.Success)
            {
                throw new InvalidOperationException("network down");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeProvider(CadProvider provider, bool success) : IProviderAdapter
    {
        public CadProvider Provider => provider;

        public Task<ProviderExecutionResult> ExecuteAsync(ConnectorJobEnvelope job, CancellationToken cancellationToken)
        {
            var result = new ProviderExecutionResult(
                IsSuccess: success,
                Message: success ? "ok" : "failed",
                ErrorCode: success ? null : "FAILED");

            return Task.FromResult(result);
        }
    }
}
