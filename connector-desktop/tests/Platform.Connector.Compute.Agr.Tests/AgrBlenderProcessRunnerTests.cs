using Platform.Connector.Compute.Agr;

namespace Platform.Connector.Compute.Agr.Tests;

public sealed class AgrBlenderProcessRunnerTests
{
    [Fact]
    public async Task ProbeAsync_WhenDisabled_DoesNotStartExternalProcess()
    {
        var options = CreateOptions();
        options.Enabled = false;
        options.BlenderExecutablePath = "missing-blender.exe";
        var runner = new AgrBlenderProcessRunner(options);

        var result = await runner.ProbeAsync(CancellationToken.None);

        Assert.False(result.Enabled);
        Assert.False(result.Ready);
        Assert.Null(result.BlenderVersion);
        Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_WhenOptInRuntimeIsConfigured_VerifiesBlender52()
    {
        var blenderPath = Environment.GetEnvironmentVariable("PLATFORM_AGR_TEST_BLENDER_PATH");
        if (string.IsNullOrWhiteSpace(blenderPath)) return;

        var options = CreateOptions();
        options.BlenderExecutablePath = blenderPath;
        var runner = new AgrBlenderProcessRunner(options);

        var result = await runner.ProbeAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.True(result.Ready, result.Message);
        Assert.StartsWith("5.2.0", result.BlenderVersion, StringComparison.Ordinal);
        Assert.Equal(AgrComputeOptions.WorkerProtocolVersion, result.WorkerVersion);
    }

    [Fact]
    public void ResolveJobFilePath_ResolvesPathInsideConfiguredStore()
    {
        var options = CreateOptions();
        var runner = new AgrBlenderProcessRunner(options);

        var result = runner.ResolveJobFilePath("job-123/publication-build.json");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(options.JobStorePath, "job-123", "publication-build.json")),
            result);
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("nested/../../outside.json")]
    public void ResolveJobFilePath_RejectsTraversalOutsideStore(string storagePath)
    {
        var runner = new AgrBlenderProcessRunner(CreateOptions());

        var exception = Assert.Throws<InvalidOperationException>(
            () => runner.ResolveJobFilePath(storagePath));

        Assert.Contains("escapes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AgrComputeOptions CreateOptions()
    {
        var options = AgrComputeOptions.CreateDefault(AppContext.BaseDirectory);
        options.JobStorePath = Path.Combine(
            Path.GetTempPath(),
            "platform-agr-runtime-tests",
            Guid.NewGuid().ToString("N"));
        return options;
    }
}
