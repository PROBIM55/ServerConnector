using System.Net;
using System.Text;
using System.Collections.Concurrent;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class IssuedControlPlaneRuntimeTests
{
    [Fact]
    public async Task IssuedModeWithoutFactory_FailsBeforeQueueOrHttp()
    {
        var root = TempPath();
        var handler = new RecordingLegacyHandler();
        await using var host = new ConnectorRuntimeHost(
            [], runtimeRootDirectory: root, httpMessageHandler: handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(IssuedOptions()));

        Assert.Equal(0, handler.Calls);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task IssuedFactoryWithoutDeviceToken_BootstrapsWithoutLegacyHttp()
    {
        var root = TempPath();
        var issued = new RecordingIssuedControlPlane();
        var handler = new RecordingLegacyHandler();
        var factoryCalls = 0;
        try
        {
            await using var host = new ConnectorRuntimeHost(
                [],
                runtimeRootDirectory: root,
                httpMessageHandler: handler,
                controlPlaneClientFactory: options =>
                {
                    factoryCalls++;
                    Assert.Equal(ConnectorAuthenticationMode.IssuedCertificate, options.AuthenticationMode);
                    Assert.Empty(options.DeviceToken);
                    return issued;
                });

            await host.StartAsync(IssuedOptions());
            await issued.SecondHeartbeatCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, factoryCalls);
            Assert.Equal(1, issued.HealthCalls);
            Assert.Equal(1, issued.BootstrapCalls);
            Assert.True(issued.HeartbeatCalls >= 2);
            Assert.Equal(0, handler.Calls);
            Assert.All(issued.ObservedTokens, Assert.Empty);
            await host.StopAsync();
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [Fact]
    public async Task IssuedFactoryReturningNull_ThrowsWithoutLegacyHttpFallback()
    {
        var root = TempPath();
        var handler = new RecordingLegacyHandler();
        Func<ConnectorRuntimeOptions, IConnectorControlPlaneClient> factory = _ => null!;
        try
        {
            await using var host = new ConnectorRuntimeHost(
                [],
                runtimeRootDirectory: root,
                httpMessageHandler: handler,
                controlPlaneClientFactory: factory);

            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(IssuedOptions()));

            Assert.Equal(0, handler.Calls);
            Assert.False(host.IsRunning);
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [Fact]
    public async Task LegacyMode_IgnoresIssuedFactory()
    {
        var root = TempPath();
        var handler = new RecordingLegacyHandler();
        var factoryCalls = 0;
        try
        {
            await using var host = new ConnectorRuntimeHost(
                [],
                runtimeRootDirectory: root,
                httpMessageHandler: handler,
                controlPlaneClientFactory: _ =>
                {
                    factoryCalls++;
                    throw new InvalidOperationException("Issued factory must not run for legacy mode.");
                });

            var options = IssuedOptions();
            options.AuthenticationMode = ConnectorAuthenticationMode.LegacyDeviceToken;
            options.DeviceToken = "legacy-token";
            await host.StartAsync(options);
            await handler.HealthCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, factoryCalls);
            Assert.True(handler.Calls > 0);
            await host.StopAsync();
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    private static ConnectorRuntimeOptions IssuedOptions() => new()
    {
        AuthenticationMode = ConnectorAuthenticationMode.IssuedCertificate,
        DeviceId = "dev-issued-1",
        DeviceToken = string.Empty,
        AgentType = "platform-cad-connector",
        Capabilities = ["autocad.build"],
        PollIntervalSeconds = 1,
        PollBackoffMaxSeconds = 2,
        HeartbeatSeconds = 30,
        EnableStatusShell = false,
        RunDemoJobWhenIdle = false,
    };

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "issued-control-plane-runtime-" + Guid.NewGuid().ToString("N"));

    private static void DeleteTemp(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class RecordingIssuedControlPlane : IConnectorControlPlaneClient
    {
        public TaskCompletionSource SecondHeartbeatCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> ObservedTokens { get; } = new();
        public int HealthCalls { get; private set; }
        public int BootstrapCalls { get; private set; }
        public int HeartbeatCalls { get; private set; }

        public Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            ObservedTokens.Enqueue(options.DeviceToken);
            HealthCalls++;
            return Task.CompletedTask;
        }

        public Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            ObservedTokens.Enqueue(options.DeviceToken);
            BootstrapCalls++;
            return Task.FromResult(new BootstrapResponseDto
            {
                Ok = true,
                DeviceId = "dev-issued-1",
                SessionId = "session-issued-1",
                HeartbeatSeconds = 30,
            });
        }

        public Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
        {
            ObservedTokens.Enqueue(options.DeviceToken);
            HeartbeatCalls++;
            if (HeartbeatCalls >= 2) SecondHeartbeatCalled.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<ConnectorJobEnvelope?>(null);

        public Task SendJobStatusAsync(
            ConnectorRuntimeOptions options,
            ConnectorJobStatusEnvelope status,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingLegacyHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public TaskCompletionSource HealthCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri?.AbsolutePath.EndsWith("/health", StringComparison.Ordinal) == true)
            {
                HealthCalled.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
            if (request.RequestUri?.AbsolutePath.EndsWith("/connect/bootstrap", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(JsonResponse(
                    "{\"ok\":true,\"session_id\":\"legacy-session\",\"device_id\":\"legacy-device\",\"heartbeat_seconds\":30}"));
            }
            if (request.RequestUri?.AbsolutePath.Contains("/connector/jobs/next", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
