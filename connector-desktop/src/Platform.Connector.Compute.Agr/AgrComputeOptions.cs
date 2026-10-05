namespace Platform.Connector.Compute.Agr;

public sealed class AgrComputeOptions
{
    public const string ExecutorId = "compute.agr.blender";
    public const string RuntimeVersion = "5.2.0";
    public const string WorkerProtocolVersion = "0.2.0";

    public bool Enabled { get; set; } = true;
    public string BlenderExecutablePath { get; set; } = string.Empty;
    public string WorkerScriptPath { get; set; } = string.Empty;
    public string JobStorePath { get; set; } = string.Empty;
    public int PreflightTimeoutSeconds { get; set; } = 60;
    public int BuildTimeoutMinutes { get; set; } = 120;
    public int MaxConcurrentBuilds { get; set; } = 1;

    public static AgrComputeOptions CreateDefault(string? applicationBasePath = null)
    {
        var appBase = Path.GetFullPath(applicationBasePath ?? AppContext.BaseDirectory);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var runtimeRoot = Path.Combine(localAppData, "Platform", "Runtimes", "Blender", RuntimeVersion);
        return new AgrComputeOptions
        {
            BlenderExecutablePath = Path.Combine(
                runtimeRoot,
                $"blender-{RuntimeVersion}-windows-x64",
                "blender.exe"),
            WorkerScriptPath = Path.Combine(appBase, "agr-blender", "agr_worker.py"),
            JobStorePath = Path.Combine(localAppData, "Platform", "AgrJobs"),
        };
    }
}
