using System.Net;
using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsEnvironment;
using Xunit;

namespace Connector.Upgrade.WindowsEnvironment.Tests;

public sealed class WindowsHostEnvironmentPortTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";

    [Fact]
    public async Task RefusesMsiDrainWhileEitherOldProcessIsRunning()
    {
        var processes = new FakeProcesses();
        processes.Platform = new LegacyProcessProbeResult(true, [42]);
        var port = new WindowsHostEnvironmentPort(Sid, new FakeSchema(true), processes, () => Sid);

        var readiness = await port.InspectReadinessAsync(CancellationToken.None);

        Assert.True(readiness.NewServerSchemaAvailable);
        Assert.False(readiness.LegacyWorkCanDrainSafely);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await port.DrainLegacyApplicationAsync(LegacyApplicationKind.PlatformConnector, CancellationToken.None));
    }

    [Fact]
    public async Task RechecksProcessAbsenceAndSidImmediatelyBeforeRemoval()
    {
        var processes = new FakeProcesses();
        var currentSid = Sid;
        var port = new WindowsHostEnvironmentPort(Sid, new FakeSchema(true), processes, () => currentSid);
        Assert.True((await port.InspectReadinessAsync(CancellationToken.None)).LegacyWorkCanDrainSafely);

        processes.Structura = new LegacyProcessProbeResult(true, [53]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await port.DrainLegacyApplicationAsync(LegacyApplicationKind.StructuraConnector, CancellationToken.None));

        processes.Structura = new LegacyProcessProbeResult(true, []);
        var proof = await port.DrainLegacyApplicationAsync(LegacyApplicationKind.StructuraConnector, CancellationToken.None);
        Assert.True(proof.Drained);
        Assert.Equal(LegacyApplicationKind.StructuraConnector, proof.Kind);
        Assert.NotEmpty(proof.EvidenceId);

        currentSid = "S-1-5-21-100-200-300-1002";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await port.DrainLegacyApplicationAsync(LegacyApplicationKind.StructuraConnector, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownProcessInventoryOrMissingServerRouteFailsClosed()
    {
        var processes = new FakeProcesses
        {
            Structura = new LegacyProcessProbeResult(false, []),
        };
        var port = new WindowsHostEnvironmentPort(Sid, new FakeSchema(false), processes, () => Sid);

        var readiness = await port.InspectReadinessAsync(CancellationToken.None);

        Assert.False(readiness.NewServerSchemaAvailable);
        Assert.False(readiness.LegacyWorkCanDrainSafely);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await port.DrainLegacyApplicationAsync(LegacyApplicationKind.StructuraConnector, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{\"code\":\"device_unauthorized\"}", true)]
    [InlineData(HttpStatusCode.NotFound, "{\"code\":\"device_unauthorized\"}", false)]
    [InlineData(HttpStatusCode.Redirect, "{\"code\":\"device_unauthorized\"}", false)]
    [InlineData(HttpStatusCode.Unauthorized, "{\"code\":\"different_service\"}", false)]
    public async Task ExactUnauthenticatedPlatformRouteProvesSchema(
        HttpStatusCode status, string body, bool expected)
    {
        var handler = new StaticResponseHandler(status, body);
        using var client = new HttpClient(handler);
        var probe = new HttpPlatformAccessSchemaProbe(client,
            new Uri("https://platform.example/api/platform/connector/access/v1/profile"));

        Assert.Equal(expected, await probe.IsAvailableAsync(CancellationToken.None));
        Assert.Equal("/api/platform/connector/access/v1/profile", handler.RequestPath);
    }

    private sealed class FakeSchema(bool available) : IPlatformAccessSchemaProbe
    {
        public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(available);
    }

    private sealed class FakeProcesses : ILegacyProcessProbe
    {
        public LegacyProcessProbeResult Structura { get; set; } = new(true, []);
        public LegacyProcessProbeResult Platform { get; set; } = new(true, []);
        public LegacyProcessProbeResult Inspect(LegacyApplicationKind kind) => kind switch
        {
            LegacyApplicationKind.StructuraConnector => Structura,
            LegacyApplicationKind.PlatformConnector => Platform,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private sealed class StaticResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestPath { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }
}
