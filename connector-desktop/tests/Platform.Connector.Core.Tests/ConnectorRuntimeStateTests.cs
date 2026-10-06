using System.Text.Json;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class ConnectorRuntimeStateTests
{
    [Fact]
    public async Task TrackingControlPlaneClient_UpdatesHeartbeatAndPollState()
    {
        var state = new ConnectorRuntimeState();
        var client = new TrackingControlPlaneClient(new StubControlPlaneClient(), state);
        var options = new ConnectorRuntimeOptions
        {
            DeviceToken = "token",
            DeviceId = "device-1"
        };

        await client.CheckHealthAsync(options, CancellationToken.None);
        await client.BootstrapAsync(options, CancellationToken.None);
        await client.SendHeartbeatAsync(options, CancellationToken.None);
        var job = await client.TryPollJobAsync(options, CancellationToken.None);

        Assert.NotNull(job);
        var snapshot = state.GetSnapshot();
        Assert.True(snapshot.ServerHealthy);
        Assert.True(snapshot.BootstrapSuccessful);
        Assert.NotEqual(default, snapshot.LastHeartbeatAtUtc);
        Assert.NotEqual(default, snapshot.LastPollAtUtc);
        Assert.Equal("req-1", snapshot.LastRequestId);
        Assert.Equal("shze", snapshot.LastModuleId);
        Assert.Equal("AutoCad", snapshot.LastProvider);
    }

    [Fact]
    public async Task TrackingJobStatusPublisher_StoresLastTerminalStatus()
    {
        var state = new ConnectorRuntimeState();
        var publisher = new TrackingJobStatusPublisher(new RecordingPublisher(), state);
        var status = new ConnectorJobStatusEnvelope(
            SchemaVersion: 1,
            RequestId: "req-1",
            DeviceId: "device-1",
            ModuleId: "bridge",
            Provider: CadProvider.Tekla,
            Status: JobStatus.Success,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: "ok");

        await publisher.PublishAsync(status, CancellationToken.None);

        var snapshot = state.GetSnapshot();
        Assert.Equal("req-1", snapshot.LastRequestId);
        Assert.Equal("bridge", snapshot.LastModuleId);
        Assert.Equal("Tekla", snapshot.LastProvider);
        Assert.Equal("Success", snapshot.LastJobStatus);
        Assert.Equal("ok", snapshot.LastJobMessage);
    }

    private sealed class StubControlPlaneClient : IConnectorControlPlaneClient
    {
        public Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => Task.FromResult(new BootstrapResponseDto
            {
                Ok = true,
                DeviceId = options.DeviceId,
                SessionId = "session-1",
                HeartbeatSeconds = 10
            });

        public Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            using var payload = JsonDocument.Parse("""{"demo":true}""");
            return Task.FromResult<ConnectorJobEnvelope?>(new ConnectorJobEnvelope(
                SchemaVersion: 1,
                RequestId: "req-1",
                ModuleId: "shze",
                Provider: CadProvider.AutoCad,
                Operation: JobOperation.Build,
                CreatedAtUtc: DateTime.UtcNow,
                Payload: payload.RootElement.Clone()));
        }

        public Task SendJobStatusAsync(ConnectorRuntimeOptions options, ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecordingPublisher : IJobStatusPublisher
    {
        public Task PublishAsync(ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
