using System.Text.Json;
using Platform.Connector.Core;

namespace Platform.Connector.Compute.Agr.Tests;

public sealed class AgrJobExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_WhenManifestPathIsMissing_ReturnsStablePayloadError()
    {
        var executor = new AgrJobExecutor(new AgrBlenderProcessRunner(CreateOptions()));
        var job = CreateJob(JsonSerializer.SerializeToElement(new { }));

        var result = await executor.ExecuteAsync(job, progress: null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("AGR_PAYLOAD_INVALID", result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithOptInBlenderAndManifest_ProducesTerminalResultAndProgress()
    {
        var blenderPath = Environment.GetEnvironmentVariable("PLATFORM_AGR_TEST_BLENDER_PATH");
        var manifestPath = Environment.GetEnvironmentVariable("PLATFORM_AGR_TEST_JOB_MANIFEST");
        if (string.IsNullOrWhiteSpace(blenderPath) || string.IsNullOrWhiteSpace(manifestPath)) return;

        var fullManifestPath = Path.GetFullPath(manifestPath);
        var options = CreateOptions();
        options.BlenderExecutablePath = blenderPath;
        options.JobStorePath = Path.GetDirectoryName(fullManifestPath)!;
        var executor = new AgrJobExecutor(new AgrBlenderProcessRunner(options));
        var progressValues = new List<int>();
        var job = CreateJob(JsonSerializer.SerializeToElement(new
        {
            manifestStoragePath = Path.GetFileName(fullManifestPath)
        }));

        var result = await executor.ExecuteAsync(
            job,
            (update, _) =>
            {
                progressValues.Add(update.Progress);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Null(result.ErrorCode);
        Assert.NotNull(result.Result);
        Assert.True(result.Result!.Value.GetProperty("ok").GetBoolean());
        Assert.NotEmpty(progressValues);
        Assert.All(progressValues, value => Assert.InRange(value, 5, 95));
    }

    private static ConnectorJobEnvelope CreateJob(JsonElement payload)
        => new(
            SchemaVersion: 2,
            RequestId: $"agr-test-{Guid.NewGuid():N}",
            ModuleId: "agr-publication",
            Provider: CadProvider.AutoCad,
            Operation: JobOperation.Export,
            CreatedAtUtc: DateTime.UtcNow,
            Payload: payload,
            ExecutorId: AgrComputeOptions.ExecutorId);

    private static AgrComputeOptions CreateOptions()
    {
        var options = AgrComputeOptions.CreateDefault(AppContext.BaseDirectory);
        options.JobStorePath = Path.Combine(
            Path.GetTempPath(),
            "platform-agr-executor-tests",
            Guid.NewGuid().ToString("N"));
        return options;
    }
}
