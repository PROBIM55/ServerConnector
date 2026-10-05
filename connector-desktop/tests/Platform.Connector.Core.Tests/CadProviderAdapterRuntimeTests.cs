using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Platform.Cad.AutoCad.Adapter;
using Platform.Cad.Tekla.Adapter;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class CadProviderAdapterRuntimeTests
{
    [Fact]
    public async Task AutoCadAdapter_ShouldFail_WhenScriptAndPluginAreMissing()
    {
        var adapter = new AutoCadProviderAdapter();
        var missingScript = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.scr");
        var payloadJson = JsonSerializer.Serialize(new
        {
            autoCadExePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            scriptPath = missingScript
        });
        var payload = JsonDocument.Parse(payloadJson).RootElement.Clone();
        var job = CreateJob("autocad-missing-script", CadProvider.AutoCad, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("PLUGIN_NOT_FOUND", result.ErrorCode);
    }

    // ────────── Tekla: HTTP forwarder contract (Phase 5) ──────────

    [Fact]
    public async Task TeklaAdapter_ShouldFail_WhenEndpointMissing()
    {
        var adapter = NewAdapterWithFakeBridge(new FakeBridgeHandler());

        var payload = JsonElementFromAnonymous(new { body = new { } });
        var job = CreateJob("missing-endpoint", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_PAYLOAD_INVALID", result.ErrorCode);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldFail_WhenTokenMissing()
    {
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => null,
            httpClientFactory: _ => new HttpClient(new FakeBridgeHandler()));

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = new { componentType = "BridgeGirder" }
        });
        var job = CreateJob("missing-token", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_TOKEN_UNAVAILABLE", result.ErrorCode);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldFail_WhenBridgeUnreachable()
    {
        var handler = new FakeBridgeHandler { HealthStatus = HttpStatusCode.ServiceUnavailable };
        var adapter = NewAdapterWithFakeBridge(handler);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = new { componentType = "BridgeGirder" }
        });
        var job = CreateJob("bridge-down", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_DESKTOP_UNREACHABLE", result.ErrorCode);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldForwardBody_AndAttachToken()
    {
        var handler = new FakeBridgeHandler();
        var adapter = NewAdapterWithFakeBridge(handler, token: "my-token-xyz");

        var bodyObj = new { componentType = "BridgeGirder", schemaVersion = 1, externalObjectId = "ext-1" };
        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = bodyObj,
            diagnostics = true
        });
        var job = CreateJob("forward-success", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("/component/upsert", handler.LastRequest!.Path);
        Assert.Equal("my-token-xyz", handler.LastRequest!.Token);
        Assert.Contains("\"BridgeGirder\"", handler.LastRequest.Body);
        Assert.Contains("\"ext-1\"", handler.LastRequest.Body);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldMapErrorCode_FromBridgeJson()
    {
        var handler = new FakeBridgeHandler
        {
            ResponseStatus = HttpStatusCode.NotFound,
            ResponseBody = """{"ok":false,"errorCode":"TEKLA_OBJECT_NOT_FOUND","message":"not in model"}""",
        };
        var adapter = NewAdapterWithFakeBridge(handler);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/modify",
            body = new { externalObjectId = "missing-id" }
        });
        var job = CreateJob("bridge-error-mapping", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TEKLA_OBJECT_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldHardRestart_OnBridgeStaleTekla_ThenRetry()
    {
        // Bridge.Desktop отвечает 503/BRIDGE_STALE_TEKLA на первый вызов
        // (внутренний reconnect не помог) и 200 OK после рестарта.
        // Connector должен вызвать restarter и повторить запрос один раз.
        var handler = new FakeBridgeHandler();
        handler.QueueResponse(HttpStatusCode.ServiceUnavailable,
            """{"ok":false,"errorCode":"BRIDGE_STALE_TEKLA","message":"stale handles"}""");
        handler.QueueResponse(HttpStatusCode.OK, """{"ok":true,"recovered":true}""");

        var restarter = new FakeRestarter { IsAvailable = true, ReturnValue = true };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = new { externalObjectId = "ext-1" }
        });
        var job = CreateJob("stale-recovery", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.True(result.IsSuccess, $"expected recovered success, got {result.ErrorCode}");
        Assert.Equal(1, restarter.CallCount);
        Assert.Equal(2, handler.PostCallCount); // первый 503 + retry 200
    }

    [Fact]
    public async Task TeklaAdapter_ShouldNotRetry_WhenRestarterUnavailable()
    {
        // На non-Windows / в тестах с NoopRestarter — recovery не делается,
        // результат BRIDGE_STALE_TEKLA пробрасывается наружу без retry.
        var handler = new FakeBridgeHandler
        {
            ResponseStatus = HttpStatusCode.ServiceUnavailable,
            ResponseBody = """{"ok":false,"errorCode":"BRIDGE_STALE_TEKLA","message":"stale"}""",
        };
        var restarter = new FakeRestarter { IsAvailable = false };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = new { externalObjectId = "ext-1" }
        });
        var job = CreateJob("stale-no-restarter", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_STALE_TEKLA", result.ErrorCode);
        Assert.Equal(0, restarter.CallCount);
        Assert.Equal(1, handler.PostCallCount);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldRestartVersionedBridgeProfile_WhenTeklaHandleIsStale()
    {
        var handler = new FakeBridgeHandler
        {
            ResponseStatus = HttpStatusCode.ServiceUnavailable,
            ResponseBody = """{"ok":false,"errorCode":"BRIDGE_STALE_TEKLA","message":"stale"}""",
        };
        var restarter = new FakeRestarter { IsAvailable = true, ReturnValue = true };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            bridgeBaseUrl = "http://127.0.0.1:39420",
            body = new { externalObjectId = "ext-1" }
        });
        var job = CreateJob("stale-versioned-profile", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_STALE_TEKLA", result.ErrorCode);
        Assert.Equal(1, restarter.CallCount);
        Assert.Equal(2, handler.PostCallCount);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldNotHardRestart_ForCapabilitiesProbe()
    {
        var handler = new FakeBridgeHandler
        {
            ResponseStatus = HttpStatusCode.ServiceUnavailable,
            ResponseBody = """{"ok":false,"errorCode":"BRIDGE_STALE_TEKLA","message":"stale"}""",
        };
        var restarter = new FakeRestarter { IsAvailable = true, ReturnValue = true };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/capabilities",
            body = new { }
        });
        var job = CreateJob("stale-capabilities", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_STALE_TEKLA", result.ErrorCode);
        Assert.Equal(0, restarter.CallCount);
        Assert.Equal(1, handler.PostCallCount);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldStartVersionedProfile_ForHealthProbeWhenItIsDown()
    {
        var handler = new FakeBridgeHandler { HealthStatus = HttpStatusCode.ServiceUnavailable };
        var restarter = new FakeRestarter
        {
            IsAvailable = true,
            ReturnValue = true,
            OnRestart = _ => handler.HealthStatus = HttpStatusCode.OK,
        };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);
        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/health",
            bridgeBaseUrl = "http://127.0.0.1:39420",
            body = new { }
        });

        var result = await adapter.ExecuteAsync(
            CreateJob("health-start-versioned-profile", CadProvider.Tekla, payload),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, restarter.CallCount);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldNotStartDefaultProfile_ForHealthProbeWhenItIsDown()
    {
        var handler = new FakeBridgeHandler { HealthStatus = HttpStatusCode.ServiceUnavailable };
        var restarter = new FakeRestarter { IsAvailable = true, ReturnValue = true };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);
        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/health",
            bridgeBaseUrl = TeklaProviderAdapter.DefaultBridgeBaseUrl,
            body = new { }
        });

        var result = await adapter.ExecuteAsync(
            CreateJob("health-do-not-start-default-profile", CadProvider.Tekla, payload),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_DESKTOP_UNREACHABLE", result.ErrorCode);
        Assert.Equal(0, restarter.CallCount);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldReturnFinalFailure_WhenRetryAlsoFails()
    {
        // Restart прошёл, но retry тоже падает (e.g. 503 второй раз) —
        // возвращаем последний результат без бесконечного цикла.
        var handler = new FakeBridgeHandler();
        handler.QueueResponse(HttpStatusCode.ServiceUnavailable,
            """{"ok":false,"errorCode":"BRIDGE_STALE_TEKLA","message":"stale1"}""");
        handler.QueueResponse(HttpStatusCode.ServiceUnavailable,
            """{"ok":false,"errorCode":"BRIDGE_STALE_TEKLA","message":"stale2"}""");
        var restarter = new FakeRestarter { IsAvailable = true, ReturnValue = true };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = new { externalObjectId = "ext-1" }
        });
        var job = CreateJob("stale-retry-fails", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_STALE_TEKLA", result.ErrorCode);
        Assert.Equal(1, restarter.CallCount);
        Assert.Equal(2, handler.PostCallCount); // не уходим в бесконечный цикл
    }

    [Fact]
    public async Task TeklaAdapter_ShouldMapBridgeUnauthorized()
    {
        var handler = new FakeBridgeHandler { ResponseStatus = HttpStatusCode.Unauthorized, ResponseBody = "" };
        var adapter = NewAdapterWithFakeBridge(handler);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = new { x = 1 }
        });
        var job = CreateJob("bridge-401", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_UNAUTHORIZED", result.ErrorCode);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldReadCapabilities_WithGet()
    {
        var handler = new FakeBridgeHandler();
        var adapter = NewAdapterWithFakeBridge(handler);
        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/capabilities",
            bridgeBaseUrl = "http://127.0.0.1:39420",
            body = new { }
        });
        var job = CreateJob("bridge-capabilities", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Get, handler.LastRequest?.Method);
        Assert.Equal("/capabilities", handler.LastRequest?.Path);
    }

    // ────────── helpers ──────────

    private static TeklaProviderAdapter NewAdapterWithFakeBridge(FakeBridgeHandler handler, string token = "fake-token")
        => new(
            tokenLoader: _ => token,
            httpClientFactory: _ => new HttpClient(handler));

    private static JsonElement JsonElementFromAnonymous(object src)
    {
        var json = JsonSerializer.Serialize(src);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static ConnectorJobEnvelope CreateJob(string requestId, CadProvider provider, JsonElement payload)
    {
        return new ConnectorJobEnvelope(
            SchemaVersion: 1,
            RequestId: requestId,
            ModuleId: "bridge",
            Provider: provider,
            Operation: JobOperation.Build,
            CreatedAtUtc: DateTime.UtcNow,
            Payload: payload,
            CorrelationId: $"corr-{requestId}");
    }

    private sealed class FakeBridgeHandler : HttpMessageHandler
    {
        public HttpStatusCode HealthStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = """{"ok":true,"echoed":"bridge"}""";

        public sealed record CapturedRequest(HttpMethod Method, string Path, string Token, string Body);
        public CapturedRequest? LastRequest { get; private set; }
        public int PostCallCount { get; private set; }

        private readonly Queue<(HttpStatusCode Status, string Body)> _queued = new();
        public void QueueResponse(HttpStatusCode status, string body) => _queued.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/health")
            {
                return new HttpResponseMessage(HealthStatus)
                {
                    Content = new StringContent("""{"ok":true,"teklaConnected":true}""", Encoding.UTF8, "application/json"),
                };
            }

            var token = request.Headers.TryGetValues("X-Bridge-Token", out var values)
                ? string.Join(",", values)
                : "";
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequest = new CapturedRequest(request.Method, path, token, body);
            PostCallCount++;

            var (status, responseBody) = _queued.Count > 0
                ? _queued.Dequeue()
                : (ResponseStatus, ResponseBody);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FakeRestarter : IBridgeDesktopRestarter
    {
        public bool IsAvailable { get; set; } = true;
        public bool ReturnValue { get; set; } = true;
        public int CallCount { get; private set; }
        public Action<string>? OnRestart { get; set; }

        public Task<bool> RestartAndWaitAsync(
            string bridgeBaseUrl,
            Func<string, HttpClient> httpClientFactory,
            TimeSpan totalTimeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            OnRestart?.Invoke(bridgeBaseUrl);
            return Task.FromResult(ReturnValue);
        }
    }
}
