using Platform.Connector.Core;

namespace Platform.Connector.Compute.Agr;

public static class AgrConnectorIntegration
{
    public static AgrComputeOptions CreateOptions(string? applicationBasePath = null)
    {
        var options = AgrComputeOptions.CreateDefault(applicationBasePath);
        var executableOverride = Environment.GetEnvironmentVariable("PLATFORM_AGR_BLENDER_PATH");
        if (!string.IsNullOrWhiteSpace(executableOverride))
        {
            options.BlenderExecutablePath = executableOverride.Trim();
        }
        return options;
    }

    public static IConnectorJobExecutor CreateExecutor(AgrJobInputMaterializer? inputMaterializer = null)
        => new AgrJobExecutor(new AgrBlenderProcessRunner(CreateOptions()), inputMaterializer);

    public static AgrJobInputMaterializer CreateInputMaterializer()
        => new(CreateOptions());

    public static async Task<AgrRuntimeProbe> ApplyRuntimeCapabilityAsync(
        ConnectorRuntimeOptions connectorOptions,
        Action<string>? log,
        CancellationToken cancellationToken = default)
    {
        var options = CreateOptions();
        var runner = new AgrBlenderProcessRunner(options);
        var probe = await runner.ProbeAsync(cancellationToken);
        connectorOptions.Capabilities = ConnectorCapabilities.Apply(
            connectorOptions.Capabilities,
            AgrComputeOptions.ExecutorId,
            probe.Ready);

        log?.Invoke(probe.Ready
            ? $"AGR compute ready: Blender {probe.BlenderVersion}, worker {probe.WorkerVersion}."
            : $"AGR compute unavailable: {probe.Message}");
        return probe;
    }
}
