using System.Net;
using System.Text;
using System.Text.Json;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class HttpMtlsConnectorControlPlaneClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Bootstrap_UsesTypedSessionContractWithoutDeviceToken_AndUpdatesOptions()
    {
        var transport = new RecordingTransport(_ => JsonResponse(HttpStatusCode.OK,
            """{"schemaVersion":1,"sessionId":"session-7","deviceId":"dev-existing","accessRevision":14,"heartbeatSeconds":30}"""));
        var client = new HttpMtlsConnectorControlPlaneClient(transport, "company-1");
        var options = Options();
        options.DeviceToken = "must-never-cross-transport";

        var result = await client.BootstrapAsync(options, default);

        var call = Assert.Single(transport.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("jobs/session", call.RelativeUri);
        Assert.Empty(call.Headers);
        var body = SerializeBody(call);
        Assert.Equal(1, body.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("platform-cad-connector", body.GetProperty("agentType").GetString());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("capabilities").ValueKind);
        Assert.False(body.TryGetProperty("deviceToken", out _));
        Assert.DoesNotContain(options.DeviceToken, body.GetRawText(), StringComparison.Ordinal);
        Assert.True(result.Ok);
        Assert.Equal("dev-existing", result.DeviceId);
        Assert.Equal("session-7", options.SessionId);
        Assert.Equal("dev-existing", options.DeviceId);
        Assert.Equal(30, options.HeartbeatSeconds);
    }

    [Fact]
    public async Task HealthAndHeartbeat_UseExactMethodsAndManagedSessionOnly()
    {
        var transport = new RecordingTransport(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = new HttpMtlsConnectorControlPlaneClient(transport, "company-1");
        var options = Options();

        await client.CheckHealthAsync(options, default);
        await client.SendHeartbeatAsync(options, default);

        Assert.Collection(transport.Calls,
            health =>
            {
                Assert.Equal(HttpMethod.Head, health.Method);
                Assert.Equal("jobs/health", health.RelativeUri);
                Assert.Null(health.Body);
                Assert.Empty(health.Headers);
            },
            heartbeat =>
            {
                Assert.Equal(HttpMethod.Post, heartbeat.Method);
                Assert.Equal("jobs/heartbeat", heartbeat.RelativeUri);
                Assert.Equal("session-1", heartbeat.Headers["X-Device-Session"]);
                Assert.DoesNotContain(heartbeat.Headers.Keys, key => key.Contains("Token", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(1, SerializeBody(heartbeat).GetProperty("schemaVersion").GetInt32());
            });
    }

    [Fact]
    public async Task LegacyAuthentication_IsRejectedBeforeTransport()
    {
        var transport = new RecordingTransport(_ => throw new InvalidOperationException("must not send"));
        var client = new HttpMtlsConnectorControlPlaneClient(transport, "company-1");
        var options = Options();
        options.AuthenticationMode = ConnectorAuthenticationMode.LegacyDeviceToken;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CheckHealthAsync(options, default));

        Assert.Empty(transport.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Poll_AcceptsOnlyExpectedCompanyProjectScope_AndEncodesAgentType(int jobSchemaVersion)
    {
        var transport = new RecordingTransport(_ => JsonResponse(HttpStatusCode.OK, JobJson(
            """{"productId":"platform","tenantId":"company-1","scopeKind":"project","projectId":"project-7"}""", jobSchemaVersion)));
        var client = new HttpMtlsConnectorControlPlaneClient(transport, "company-1");
        var options = Options();
        options.AgentType = "cad connector/1";

        var job = await client.TryPollJobAsync(options, default);

        Assert.NotNull(job);
        Assert.Equal(ConnectorScopeKind.Project, job.Scope!.ScopeKind);
        var call = Assert.Single(transport.Calls);
        Assert.Equal("jobs/next?agentType=cad%20connector%2F1", call.RelativeUri);
        Assert.Equal("session-1", call.Headers["X-Device-Session"]);
    }

    [Theory]
    [InlineData("{\"productId\":\"platform\",\"tenantId\":null,\"scopeKind\":\"deviceLocal\",\"projectId\":null}")]
    [InlineData("{\"productId\":\"platform\",\"tenantId\":\"other-company\",\"scopeKind\":\"project\",\"projectId\":\"project-7\"}")]
    public async Task Poll_RejectsUnauthorizedScope(string scope)
    {
        var transport = new RecordingTransport(_ => JsonResponse(HttpStatusCode.OK, JobJson(scope)));
        var client = new HttpMtlsConnectorControlPlaneClient(transport, "company-1");

        await Assert.ThrowsAsync<InvalidDataException>(() => client.TryPollJobAsync(Options(), default));
    }

    [Fact]
    public async Task Poll_RejectsMissingScopeAndUnknownProduct()
    {
        var missing = new RecordingTransport(_ => JsonResponse(HttpStatusCode.OK, JobJson("null")));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HttpMtlsConnectorControlPlaneClient(missing, "company-1").TryPollJobAsync(Options(), default));

        var unknown = new RecordingTransport(_ => JsonResponse(HttpStatusCode.OK, JobJson(
            """{"productId":"unknown","tenantId":"company-1","scopeKind":"project","projectId":"project-7"}""")));
        await Assert.ThrowsAsync<JsonException>(() =>
            new HttpMtlsConnectorControlPlaneClient(unknown, "company-1").TryPollJobAsync(Options(), default));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":0,\"sessionId\":\"s\",\"deviceId\":\"dev-existing\",\"accessRevision\":1,\"heartbeatSeconds\":30}")]
    [InlineData("{\"schemaVersion\":1,\"sessionId\":\"s\",\"deviceId\":\"\",\"accessRevision\":1,\"heartbeatSeconds\":30}")]
    [InlineData("{\"schemaVersion\":1,\"sessionId\":\"s\",\"deviceId\":\"dev-existing\",\"accessRevision\":0,\"heartbeatSeconds\":30}")]
    [InlineData("{\"schemaVersion\":1,\"sessionId\":\"s\",\"deviceId\":\"dev-existing\",\"accessRevision\":1,\"heartbeatSeconds\":9}")]
    [InlineData("{\"schemaVersion\":1,\"sessionId\":\"s\",\"deviceId\":\"dev-existing\",\"accessRevision\":1,\"heartbeatSeconds\":3601}")]
    public async Task Bootstrap_RejectsInvalidSessionReceipt(string json)
    {
        var transport = new RecordingTransport(_ => JsonResponse(HttpStatusCode.OK, json));
        var client = new HttpMtlsConnectorControlPlaneClient(transport, "company-1");

        await Assert.ThrowsAsync<InvalidDataException>(() => client.BootstrapAsync(Options(), default));
    }

    [Fact]
    public async Task Bootstrap_RejectsReceiptForAnotherEnrolledDevice()
    {
        var transport = new RecordingTransport(_ => JsonResponse(HttpStatusCode.OK,
            """{"schemaVersion":1,"sessionId":"session-7","deviceId":"foreign-device","accessRevision":14,"heartbeatSeconds":30}"""));
        var client = new HttpMtlsConnectorControlPlaneClient(transport, "company-1");
        var options = Options();

        await Assert.ThrowsAsync<InvalidDataException>(() => client.BootstrapAsync(options, default));

        Assert.Equal("dev-existing", options.DeviceId);
        Assert.Equal("session-1", options.SessionId);
    }

    [Theory]
    [InlineData("\"requestId\": \"request-1\"", "\"requestId\": \"\"")]
    [InlineData("\"moduleId\": \"cad\"", "\"moduleId\": \"\"")]
    [InlineData("\"provider\": \"autocad\"", "\"provider\": 99")]
    [InlineData("\"operation\": \"build\"", "\"operation\": 99")]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 3")]
    public async Task Poll_RejectsInvalidEnvelopeIdentityOrEnums(string original, string invalid)
    {
        var scope = """{"productId":"platform","tenantId":"company-1","scopeKind":"project","projectId":"project-7"}""";
        var json = JobJson(scope).Replace(original, invalid, StringComparison.Ordinal);
        var transport = new RecordingTransport(_ => JsonResponse(HttpStatusCode.OK, json));
        var client = new HttpMtlsConnectorControlPlaneClient(transport, "company-1");

        await Assert.ThrowsAsync<InvalidDataException>(() => client.TryPollJobAsync(Options(), default));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StatusBody_ExcludesClientSuppliedIdentityAndScope(int jobSchemaVersion)
    {
        var transport = new RecordingTransport(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = new HttpMtlsConnectorControlPlaneClient(transport, "company-1");
        using var result = JsonDocument.Parse("{\"artifact\":\"ok\"}");
        var status = new ConnectorJobStatusEnvelope(
            jobSchemaVersion, "request/7", "forged-device", "forged-module", CadProvider.Tekla, JobStatus.Success,
            DateTime.SpecifyKind(new DateTime(2026, 10, 2, 10, 30, 0), DateTimeKind.Utc),
            "done", 100, null, result.RootElement.Clone(), "correlation-7", "forged-executor",
            ExecutionScope.Project(ConnectorProductId.Structura, "company-1", "project-7"));

        await client.SendJobStatusAsync(Options(), status, default);

        var call = Assert.Single(transport.Calls);
        Assert.Equal("jobs/request%2F7/status", call.RelativeUri);
        var body = SerializeBody(call);
        Assert.Equal(1, body.GetProperty("schemaVersion").GetInt32());
        Assert.Equal((int)JobStatus.Success, body.GetProperty("status").GetInt32());
        foreach (var forbidden in new[] { "requestId", "deviceId", "moduleId", "provider", "executorId", "scope", "correlationId" })
        {
            Assert.False(body.TryGetProperty(forbidden, out _), forbidden);
        }
    }

    [Fact]
    public async Task ResponseOverTwoMiB_IsRejectedWithoutParsing()
    {
        var transport = new RecordingTransport(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]),
        });
        var client = new HttpMtlsConnectorControlPlaneClient(transport, "company-1");

        await Assert.ThrowsAsync<InvalidDataException>(() => client.BootstrapAsync(Options(), default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AuthenticationFailure_IsTypedAndDoesNotRetainResponseBody(HttpStatusCode statusCode)
    {
        var transport = new RecordingTransport(_ => JsonResponse(statusCode, "{\"secret\":\"must-not-escape\"}"));
        var client = new HttpMtlsConnectorControlPlaneClient(transport, "company-1");

        var exception = await Assert.ThrowsAsync<TokenRejectedException>(() => client.CheckHealthAsync(Options(), default));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Null(exception.ResponseBody);
        Assert.DoesNotContain("must-not-escape", exception.Message, StringComparison.Ordinal);
    }

    private static ConnectorRuntimeOptions Options() => new()
    {
        AuthenticationMode = ConnectorAuthenticationMode.IssuedCertificate,
        DeviceId = "dev-existing",
        DeviceToken = "legacy-secret",
        SessionId = "session-1",
        AgentType = "platform-cad-connector",
        Capabilities = ["autocad.build", "tekla.apply"],
    };

    private static string JobJson(string scope, int schemaVersion = 1) => $$"""
        {
          "schemaVersion": {{schemaVersion}},
          "requestId": "request-1",
          "moduleId": "cad",
          "provider": "autocad",
          "operation": "build",
          "createdAtUtc": "2026-10-02T10:00:00Z",
          "payload": {},
          "correlationId": "correlation-1",
          "executorId": "cad.autocad",
          "scope": {{scope}}
        }
        """;

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static JsonElement SerializeBody(TransportCall call)
    {
        Assert.NotNull(call.Body);
        return JsonSerializer.SerializeToElement(call.Body, call.Body.GetType(), JsonOptions);
    }

    private sealed class RecordingTransport(Func<TransportCall, HttpResponseMessage> responder)
        : ICommonConnectorRequestTransport
    {
        public List<TransportCall> Calls { get; } = [];

        public Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string relativeUri,
            object? body,
            IReadOnlyDictionary<string, string> managedHeaders,
            CancellationToken cancellationToken)
        {
            var call = new TransportCall(method, relativeUri, body,
                new Dictionary<string, string>(managedHeaders, StringComparer.Ordinal));
            Calls.Add(call);
            return Task.FromResult(responder(call));
        }
    }

    private sealed record TransportCall(
        HttpMethod Method,
        string RelativeUri,
        object? Body,
        IReadOnlyDictionary<string, string> Headers);
}
