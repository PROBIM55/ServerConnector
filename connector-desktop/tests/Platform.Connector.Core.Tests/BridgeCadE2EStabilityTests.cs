using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Platform.Cad.AutoCad.Adapter;
using Platform.Cad.Tekla.Adapter;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class BridgeCadE2EStabilityTests
{
    [Fact]
    public async Task AutoCadAdapter_RuntimeFlow_IsStable_ForTenRuns()
    {
        var adapter = new AutoCadProviderAdapter();
        var tempDir = Path.Combine(Path.GetTempPath(), "platform-autocad-soak", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "run.cmd");
        File.WriteAllText(scriptPath, "@echo off\r\nexit /b 0", System.Text.Encoding.ASCII);
        var statusPath = Path.Combine(tempDir, "build-status.json");

        for (var i = 0; i < 10; i++)
        {
            File.WriteAllText(statusPath, $$"""
            {
              "requestId": "autocad-stable-{{i}}",
              "status": "success",
              "message": "simulated runtime success"
            }
            """);

            var payloadJson = JsonSerializer.Serialize(new
            {
                autoCadExePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                scriptPath,
                workingDirectory = tempDir,
                buildStatusPath = statusPath,
                jobTimeoutSeconds = 15,
                diagnostics = true
            });
            using var payloadDocument = JsonDocument.Parse(payloadJson);
            var job = CreateJob($"autocad-stable-{i}", CadProvider.AutoCad, payloadDocument.RootElement.Clone());

            var result = await adapter.ExecuteAsync(job, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Null(result.ErrorCode);
        }
    }

    [Fact]
    public async Task TeklaAdapter_HttpForwarder_IsStable_ForTenRuns()
    {
        var handler = new FakeBridgeHandler();
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "stable-token",
            httpClientFactory: _ => new HttpClient(handler));

        for (var i = 0; i < 10; i++)
        {
            var payloadJson = JsonSerializer.Serialize(new
            {
                endpoint = "/component/read",
                body = new { externalObjectId = $"ext-{i}", componentType = "BridgeGirder", schemaVersion = 1 },
            });
            using var payloadDocument = JsonDocument.Parse(payloadJson);
            var job = CreateJob($"tekla-stable-{i}", CadProvider.Tekla, payloadDocument.RootElement.Clone());

            var result = await adapter.ExecuteAsync(job, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Null(result.ErrorCode);
        }

        Assert.Equal(10, handler.SuccessCalls);
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
        public int SuccessCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage resp;
            if (request.Method == HttpMethod.Get && path == "/health")
            {
                resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ok":true,"teklaConnected":true}""", Encoding.UTF8, "application/json"),
                };
            }
            else
            {
                SuccessCalls++;
                resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
                };
            }
            return Task.FromResult(resp);
        }
    }
}
