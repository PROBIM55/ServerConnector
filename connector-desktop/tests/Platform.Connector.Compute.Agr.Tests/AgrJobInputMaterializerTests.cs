using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Connector.Core;

namespace Platform.Connector.Compute.Agr.Tests;

public sealed class AgrJobInputMaterializerTests
{
    [Fact]
    public async Task MaterializeAsync_DownloadsAndVerifiesPackageFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "platform-agr-transfer-tests", Guid.NewGuid().ToString("N"));
        var bytes = Encoding.UTF8.GetBytes("immutable agr package");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var handler = new RecordingHandler(bytes);
        var options = AgrComputeOptions.CreateDefault(AppContext.BaseDirectory);
        options.JobStorePath = root;
        var materializer = new AgrJobInputMaterializer(options, new HttpClient(handler));
        materializer.Configure(new ConnectorRuntimeOptions
        {
            ServerUrl = "http://localhost:5068/api/platform",
            DeviceToken = "device-token",
            SessionId = "session-id",
        });
        var payload = JsonSerializer.SerializeToElement(new
        {
            inputFiles = new[]
            {
                new
                {
                    relativePath = "job-1/geometry.bin",
                    downloadPath = "/api/platform/connector/agr-publication/jobs/job-1/files/geometry.bin",
                    sha256 = hash,
                    byteLength = bytes.LongLength,
                },
            },
        });

        try
        {
            await materializer.MaterializeAsync(payload, CancellationToken.None);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "job-1", "geometry.bin")));
            Assert.Equal("device-token", handler.DeviceToken);
            Assert.Equal("session-id", handler.SessionId);
            Assert.Equal(
                "http://localhost:5068/api/platform/connector/agr-publication/jobs/job-1/files/geometry.bin",
                handler.RequestUri?.AbsoluteUri);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../geometry.bin")]
    [InlineData("job/../../geometry.bin")]
    public void ResolveJobFilePath_RejectsTraversal(string relativePath)
    {
        var options = AgrComputeOptions.CreateDefault(AppContext.BaseDirectory);
        options.JobStorePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var materializer = new AgrJobInputMaterializer(options);

        Assert.Throws<InvalidOperationException>(() => materializer.ResolveJobFilePath(relativePath));
    }

    private sealed class RecordingHandler(byte[] responseBytes) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? DeviceToken { get; private set; }
        public string? SessionId { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            DeviceToken = request.Headers.GetValues("X-Device-Token").Single();
            SessionId = request.Headers.GetValues("X-Device-Session").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBytes),
            });
        }
    }
}
