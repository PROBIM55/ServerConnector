using System.Text.Json;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class ConnectorJobRunnerTests
{
    [Fact]
    public async Task RunOnceAsync_WithRegisteredProvider_PublishesSuccess()
    {
        var publisher = new InMemoryStatusPublisher();
        var runner = new ConnectorJobRunner(new[] { new FakeProvider(CadProvider.AutoCad, success: true) }, publisher);

        await runner.RunOnceAsync("device-1", BuildJob(CadProvider.AutoCad));

        Assert.Equal(3, publisher.Messages.Count);
        Assert.Equal(JobStatus.Picked, publisher.Messages[0].Status);
        Assert.Equal(JobStatus.Running, publisher.Messages[1].Status);
        Assert.Equal(JobStatus.Success, publisher.Messages[2].Status);
    }

    [Fact]
    public async Task RunOnceAsync_WithoutProvider_PublishesProviderMissingError()
    {
        var publisher = new InMemoryStatusPublisher();
        var runner = new ConnectorJobRunner(Array.Empty<IProviderAdapter>(), publisher);

        await runner.RunOnceAsync("device-1", BuildJob(CadProvider.Tekla));

        Assert.Equal(2, publisher.Messages.Count);
        Assert.Equal(JobStatus.Picked, publisher.Messages[0].Status);
        Assert.Equal(JobStatus.Error, publisher.Messages[1].Status);
        Assert.Equal("CONNECTOR_PROVIDER_MISSING", publisher.Messages[1].ErrorCode);
    }

    [Fact]
    public async Task RunOnceAsync_WhenProviderThrows_PublishesUnhandledExceptionError()
    {
        var publisher = new InMemoryStatusPublisher();
        var runner = new ConnectorJobRunner(new[] { new ThrowingProvider(CadProvider.AutoCad) }, publisher);

        await runner.RunOnceAsync("device-1", BuildJob(CadProvider.AutoCad));

        Assert.Equal(3, publisher.Messages.Count);
        Assert.Equal(JobStatus.Picked, publisher.Messages[0].Status);
        Assert.Equal(JobStatus.Running, publisher.Messages[1].Status);
        Assert.Equal(JobStatus.Error, publisher.Messages[2].Status);
        Assert.Equal("UNHANDLED_EXCEPTION", publisher.Messages[2].ErrorCode);
    }

    [Fact]
    public async Task RunOnceAsync_WhenCancelled_PublishesCancelledStatus()
    {
        var publisher = new InMemoryStatusPublisher();
        var runner = new ConnectorJobRunner(new[] { new CancellableProvider(CadProvider.AutoCad) }, publisher);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await runner.RunOnceAsync("device-1", BuildJob(CadProvider.AutoCad), cts.Token);

        Assert.Equal(3, publisher.Messages.Count);
        Assert.Equal(JobStatus.Picked, publisher.Messages[0].Status);
        Assert.Equal(JobStatus.Running, publisher.Messages[1].Status);
        Assert.Equal(JobStatus.Cancelled, publisher.Messages[2].Status);
    }

    [Fact]
    public async Task RunOnceAsync_WithExplicitExecutor_RoutesWithoutChangingCadProvider()
    {
        var publisher = new InMemoryStatusPublisher();
        var executor = new FakeExecutor("compute.agr.blender");
        var runner = new ConnectorJobRunner(
            Array.Empty<IProviderAdapter>(),
            publisher,
            executors: [executor]);
        var job = BuildJob(CadProvider.AutoCad) with
        {
            SchemaVersion = 2,
            ExecutorId = "compute.agr.blender"
        };

        var terminal = await runner.RunOnceAsync("device-1", job);

        Assert.Equal(JobStatus.Success, terminal.Status);
        Assert.Equal("compute.agr.blender", terminal.ExecutorId);
        Assert.Equal(CadProvider.AutoCad, terminal.Provider);
        Assert.Same(job, executor.ReceivedJob);
        Assert.Contains(publisher.Messages, message =>
            message.Status == JobStatus.Running && message.Progress == 55);
    }

    [Fact]
    public async Task RunOnceAsync_WithMissingExplicitExecutor_ReturnsExecutorError()
    {
        var publisher = new InMemoryStatusPublisher();
        var runner = new ConnectorJobRunner(Array.Empty<IProviderAdapter>(), publisher);
        var job = BuildJob(CadProvider.AutoCad) with
        {
            SchemaVersion = 2,
            ExecutorId = "compute.agr.blender"
        };

        var terminal = await runner.RunOnceAsync("device-1", job);

        Assert.Equal(JobStatus.Error, terminal.Status);
        Assert.Equal("CONNECTOR_EXECUTOR_MISSING", terminal.ErrorCode);
    }

    [Fact]
    public async Task RunOnceAsync_LegacyCadJob_UsesStableExecutorId()
    {
        var publisher = new InMemoryStatusPublisher();
        var runner = new ConnectorJobRunner(new[] { new FakeProvider(CadProvider.Tekla, success: true) }, publisher);

        var terminal = await runner.RunOnceAsync("device-1", BuildJob(CadProvider.Tekla));

        Assert.Equal("cad.tekla", terminal.ExecutorId);
        Assert.Equal(1, terminal.SchemaVersion);
    }

    private static ConnectorJobEnvelope BuildJob(CadProvider provider)
    {
        using var payload = JsonDocument.Parse("""{"value":1}""");
        return new ConnectorJobEnvelope(
            SchemaVersion: 1,
            RequestId: "req-1",
            ModuleId: "shze",
            Provider: provider,
            Operation: JobOperation.Build,
            CreatedAtUtc: DateTime.UtcNow,
            Payload: payload.RootElement.Clone());
    }

    private sealed class InMemoryStatusPublisher : IJobStatusPublisher
    {
        public List<ConnectorJobStatusEnvelope> Messages { get; } = new();

        public Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
        {
            Messages.Add(status);
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

    private sealed class ThrowingProvider(CadProvider provider) : IProviderAdapter
    {
        public CadProvider Provider => provider;

        public Task<ProviderExecutionResult> ExecuteAsync(ConnectorJobEnvelope job, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class CancellableProvider(CadProvider provider) : IProviderAdapter
    {
        public CadProvider Provider => provider;

        public Task<ProviderExecutionResult> ExecuteAsync(ConnectorJobEnvelope job, CancellationToken cancellationToken)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class FakeExecutor(string executorId) : IConnectorJobExecutor
    {
        public string ExecutorId => executorId;
        public ConnectorJobEnvelope? ReceivedJob { get; private set; }

        public async Task<ConnectorExecutionResult> ExecuteAsync(
            ConnectorJobEnvelope job,
            Func<ConnectorExecutionProgress, CancellationToken, Task>? progress,
            CancellationToken cancellationToken)
        {
            ReceivedJob = job;
            if (progress is not null)
            {
                await progress(new ConnectorExecutionProgress(55, "half"), cancellationToken);
            }
            return new ConnectorExecutionResult(true, "ok");
        }
    }
}
