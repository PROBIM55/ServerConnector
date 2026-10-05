namespace Platform.Connector.Compute.Agr;

public sealed class AgrManagedRuntime
{
    private const string RuntimeManifestFileName = "blender-runtime-5.2.0-win-x64.json";
    private readonly HttpClient _httpClient;
    private readonly string _applicationBasePath;
    private readonly string? _runtimeRoot;

    public AgrManagedRuntime(
        HttpClient httpClient,
        string? applicationBasePath = null,
        string? runtimeRoot = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _applicationBasePath = Path.GetFullPath(applicationBasePath ?? AppContext.BaseDirectory);
        _runtimeRoot = string.IsNullOrWhiteSpace(runtimeRoot) ? null : Path.GetFullPath(runtimeRoot);
    }

    public string ManifestPath => Path.Combine(
        _applicationBasePath,
        "agr-blender",
        RuntimeManifestFileName);

    public Task<AgrRuntimeProbe> ProbeAsync(CancellationToken cancellationToken = default)
        => new AgrBlenderProcessRunner(CreateOptions()).ProbeAsync(cancellationToken);

    public async Task<AgrManagedRuntimeInstallResult> EnsureInstalledAsync(
        IProgress<AgrRuntimeInstallProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var manifest = AgrBlenderRuntimeManifest.Load(ManifestPath);
        var installer = new AgrBlenderRuntimeInstaller(_httpClient, _runtimeRoot);
        var install = await installer.EnsureInstalledAsync(
            manifest,
            progress,
            cancellationToken,
            async (stagedExecutable, validationCancellationToken) =>
            {
                var stagedOptions = CreateOptions();
                stagedOptions.BlenderExecutablePath = stagedExecutable;
                var stagedProbe = await new AgrBlenderProcessRunner(stagedOptions)
                    .ProbeAsync(validationCancellationToken);
                EnsureCompatible(stagedProbe);
            });

        var finalProbe = await ProbeAsync(cancellationToken);
        EnsureCompatible(finalProbe);
        return new AgrManagedRuntimeInstallResult(install, finalProbe);
    }

    private AgrComputeOptions CreateOptions()
    {
        var options = AgrComputeOptions.CreateDefault(_applicationBasePath);
        if (_runtimeRoot is not null)
        {
            options.BlenderExecutablePath = Path.Combine(
                _runtimeRoot,
                AgrComputeOptions.RuntimeVersion,
                $"blender-{AgrComputeOptions.RuntimeVersion}-windows-x64",
                "blender.exe");
        }
        return options;
    }

    private static void EnsureCompatible(AgrRuntimeProbe probe)
    {
        if (!probe.Ready)
        {
            throw new InvalidDataException($"Blender runtime preflight failed: {probe.Message}");
        }
        if (!IsCompatibleBlenderVersion(probe.BlenderVersion))
        {
            throw new InvalidDataException(
                $"Expected Blender {AgrComputeOptions.RuntimeVersion}, got {probe.BlenderVersion ?? "unknown"}.");
        }
        if (!string.Equals(
                probe.WorkerVersion,
                AgrComputeOptions.WorkerProtocolVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Expected AGR worker {AgrComputeOptions.WorkerProtocolVersion}, got {probe.WorkerVersion ?? "unknown"}.");
        }
    }

    public static bool IsCompatibleBlenderVersion(string? actualVersion)
    {
        if (string.IsNullOrWhiteSpace(actualVersion)
            || !Version.TryParse(AgrComputeOptions.RuntimeVersion, out var expected))
        {
            return false;
        }

        var numericVersion = actualVersion
            .Trim()
            .Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return Version.TryParse(numericVersion, out var actual)
            && actual.Major == expected.Major
            && actual.Minor == expected.Minor
            && actual.Build == expected.Build;
    }
}

public sealed record AgrManagedRuntimeInstallResult(
    AgrRuntimeInstallResult Install,
    AgrRuntimeProbe Probe);
