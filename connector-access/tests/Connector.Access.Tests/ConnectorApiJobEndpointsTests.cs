using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Data.Common;
using Connector.Access.AspNetCore;
using Connector.Access.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Connector.Access.Tests;

public sealed class ConnectorApiJobEndpointsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SessionHeartbeatPollAcceptsMatchingJobStatusVersionWhileKeepingV1SessionWireContract(int jobSchemaVersion)
    {
        var profile = Profile();
        var authorizer = new FixtureAuthorizer(profile);
        var store = new FixtureStore();
        store.Job = store.Job! with { SchemaVersion = jobSchemaVersion };
        var deliveries = new FixtureDeliveryStore();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IConnectorApiJobAuthorizer>(authorizer);
        builder.Services.AddSingleton<IConnectorApiJobStore>(store);
        builder.Services.AddSingleton<IConnectorApiJobDeliveryStore>(deliveries);
        await using var app = builder.Build();
        app.MapConnectorApiJobs();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var sessionResponse = await client.PostAsJsonAsync("/api/platform/connector/access/v1/jobs/session",
                new ConnectorApiJobSessionRequest(1, " test-host ", "agent-1", "platform-cad-connector", [" CAD.Build ", "cad.build"]));
            Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
            Assert.Equal("no-store", sessionResponse.Headers.CacheControl?.ToString());
            using var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
            Assert.Equal(1, sessionJson.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("device-1", sessionJson.RootElement.GetProperty("deviceId").GetString());
            Assert.Equal(11, sessionJson.RootElement.GetProperty("accessRevision").GetInt64());
            var sessionId = sessionJson.RootElement.GetProperty("sessionId").GetString()!;
            Assert.Contains("cad.build", store.CapabilitiesJson);

            using var stalePoll = await client.GetAsync("/api/platform/connector/access/v1/jobs/next");
            Assert.Equal(HttpStatusCode.Conflict, stalePoll.StatusCode);

            using var heartbeat = new HttpRequestMessage(HttpMethod.Post, "/api/platform/connector/access/v1/jobs/heartbeat")
            {
                Content = JsonContent.Create(new ConnectorApiJobHeartbeatRequest(1, "test-host", "agent-1", "platform-cad-connector", [])),
            };
            heartbeat.Headers.Add("X-Device-Session", sessionId);
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(heartbeat)).StatusCode);

            using var poll = new HttpRequestMessage(HttpMethod.Get, "/api/platform/connector/access/v1/jobs/next?agentType=platform-cad-connector");
            poll.Headers.Add("X-Device-Session", sessionId);
            var pollResponse = await client.SendAsync(poll);
            Assert.Equal(HttpStatusCode.OK, pollResponse.StatusCode);
            Assert.Equal("no-store", pollResponse.Headers.CacheControl?.ToString());
            using var jobJson = JsonDocument.Parse(await pollResponse.Content.ReadAsStringAsync());
            Assert.Equal(jobSchemaVersion, jobJson.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("job-1", jobJson.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("platform", jobJson.RootElement.GetProperty("scope").GetProperty("productId").GetString());
            Assert.Equal("project", jobJson.RootElement.GetProperty("scope").GetProperty("scopeKind").GetString());

            using var status = new HttpRequestMessage(HttpMethod.Post, "/api/platform/connector/access/v1/jobs/job-1/status")
            {
                Content = JsonContent.Create(new { schemaVersion = jobSchemaVersion, status = 3, updatedAtUtc = DateTimeOffset.UtcNow,
                    message = "complete", progress = 100, result = new { ok = true } }),
            };
            status.Headers.Add("X-Device-Session", sessionId);
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(status)).StatusCode);
            Assert.Equal(3, deliveries.AdvancedStatus?.Status);
            Assert.Equal("{\"ok\":true}", deliveries.AdvancedStatus?.Result?.GetRawText());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task PollOutsideCurrentProjectGrantLeavesJobQueuedAndDoesNotRecordTerminalStatus()
    {
        var profile = Profile();
        var store = new FixtureStore { Job = new ConnectorApiJobEnvelope(1, "job-denied", "bridge", 0, 0,
            DateTime.UtcNow, JsonDocument.Parse("{}").RootElement.Clone(), null, null,
            new ConnectorApiJobExecutionScope("platform", "company-1", "project", "project-other")) };
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IConnectorApiJobAuthorizer>(new FixtureAuthorizer(profile));
        builder.Services.AddSingleton<IConnectorApiJobStore>(store);
        builder.Services.AddSingleton<IConnectorApiJobDeliveryStore>(new FixtureDeliveryStore());
        await using var app = builder.Build();
        app.MapConnectorApiJobs();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            store.SessionId = "session-1";
            using var poll = new HttpRequestMessage(HttpMethod.Get, "/api/platform/connector/access/v1/jobs/next");
            poll.Headers.Add("X-Device-Session", "session-1");
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(poll)).StatusCode);
            Assert.NotNull(store.Job);
            Assert.Null(store.AppendedStatus);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static DeviceAccessProfile Profile() => new(1, "device-1", "user-1", "company-1", 11, 11,
        DateTimeOffset.UtcNow.AddHours(1),
        [new ModuleGrant(ConnectorProduct.Platform, "bridge", [ConnectorPermission.Execute])],
        [new ResourceGrant("project-1", "project", "project-1", [ConnectorPermission.Execute])]);

    private sealed class FixtureAuthorizer(DeviceAccessProfile profile) : IConnectorApiJobAuthorizer
    {
        public ValueTask<ConnectorApiJobAuthorization> AuthorizeAsync(HttpContext context) => ValueTask.FromResult(
            new ConnectorApiJobAuthorization(new ConnectorPrivateAccessContext(
                new AuthenticatedDevice("device-1", "user-1", "company-1", "cert", 11), profile), null));
    }

    private sealed class FixtureStore : IConnectorApiJobStore
    {
        public string SessionId { get; set; } = string.Empty;
        public string CapabilitiesJson { get; private set; } = string.Empty;
        public ConnectorApiJobEnvelope? Job { get; set; } = new(2, "job-1", "bridge", 0, 0,
            DateTime.UtcNow, JsonDocument.Parse("{}").RootElement.Clone(), "correlation-1", "executor-1",
            new ConnectorApiJobExecutionScope("platform", "company-1", "project", "project-1"));
        public ConnectorApiJobStatusEnvelope? AppendedStatus { get; private set; }

        public bool TryGetSession(string deviceId, out string sessionId)
        {
            sessionId = SessionId;
            return SessionId.Length > 0;
        }

        public void UpsertSession(string deviceId, string sessionId, string hostname, string agentType, string moduleScope, string capabilitiesJson)
        {
            SessionId = sessionId;
            CapabilitiesJson = capabilitiesJson;
        }

        public void UpsertHeartbeat(string deviceId, string hostname, string? agentVersion, string? publicIp,
            string agentType, string moduleScope, string capabilitiesJson) => CapabilitiesJson = capabilitiesJson;

        public ConnectorApiJobEnvelope? TryTakeNextJob(string deviceId, string? agentType,
            Func<ConnectorApiJobEnvelope, bool> canExecute,
            Action<DbConnection, DbTransaction, ConnectorApiJobEnvelope> recordDelivery)
        {
            var result = Job;
            if (result is null || !canExecute(result)) return null;
            recordDelivery(null!, null!, result);
            Job = null;
            return result;
        }

        public void AppendStatus(ConnectorApiJobStatusEnvelope status) => AppendedStatus = status;
    }

    private sealed class FixtureDeliveryStore : IConnectorApiJobDeliveryStore
    {
        private ConnectorApiJobDelivery? _delivery;
        public ConnectorApiJobStatusEnvelope? AdvancedStatus { get; private set; }
        public ValueTask RecordAsync(ConnectorApiJobDelivery delivery, CancellationToken cancellationToken)
        {
            _delivery = delivery;
            return ValueTask.CompletedTask;
        }
        public void RecordInTransaction(DbConnection connection, DbTransaction transaction, ConnectorApiJobDelivery delivery)
        {
            _delivery = delivery;
        }
        public ValueTask<ConnectorApiJobDelivery?> FindAsync(string requestId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_delivery?.RequestId == requestId ? _delivery : null);
        public ValueTask<bool> AdvanceStatusAndAppendAsync(ConnectorApiJobDelivery delivery,
            ConnectorApiJobStatusEnvelope status, CancellationToken cancellationToken)
        {
            AdvancedStatus = status;
            return ValueTask.FromResult(true);
        }
    }
}
