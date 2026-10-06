using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Connector.Desktop.Models;
using Connector.Desktop.Services;
using Platform.Connector.Core;
using Velopack;

internal static class Program
{
    private const string LocalDeviceId = "update-smoke-local-device";
    private const string ExecutorId = "update-smoke.blocking";
    private const string ResultMarker = "runtime-completed-before-update";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string ExecutingVersion => typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();
        if (args.Length != 3 || args[0] is not "--write" and not "--read") return 0;
        var testRoot = Path.GetFullPath(args[1]);
        if (!testRoot.Contains("\\artifacts\\installed-update-smoke\\", StringComparison.OrdinalIgnoreCase)) return 0;
        // The established native harness uses an invisible owned HWND and its
        // own profile. Keep this entry point synchronous so WPF remains on STA.
        var uiOutput = Path.Combine(testRoot, args[0] == "--write" ? "native-ui-a" : "native-ui-b");
        if (Connector.UiSmoke.GraphiteWebSmoke.Run(uiOutput) != 0) return 4;
        return RunAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var testRoot = Path.GetFullPath(args[1]);

        var settingsPath = Path.Combine(testRoot, "profile", "ConnectorAgentDesktop", "settings.json");
        var markerPath = Path.Combine(testRoot, "probe-marker.txt");
        var runtimeRoot = Path.Combine(testRoot, "runtime");
        var evidencePath = Path.Combine(testRoot, "runtime-evidence.json");
        var settings = new SettingsService(settingsPath);
        _ = new PackageUpdateService(null, managedInstall: true).IsManagedInstall;

        if (args[0] == "--write")
        {
            settings.Save(new AppSettings
            {
                DeviceId = "update-smoke-device",
                TokenCipherBase64 = SettingsService.EncryptToken(args[2]),
                AutoStart = false,
                HeartbeatSeconds = 61,
            });
            File.WriteAllText(markerPath, args[2]);
            await RunPhaseAAsync(runtimeRoot, evidencePath);
            return 0;
        }

        try
        {
            await RunPhaseBAsync(settings, markerPath, runtimeRoot, evidencePath);
            return 0;
        }
        catch
        {
            return 3;
        }
    }

    private static async Task RunPhaseAAsync(string runtimeRoot, string evidencePath)
    {
        var executor = new BlockingExecutor();
        var statuses = new ConcurrentQueue<JobStatus>();
        await using var host = new ConnectorRuntimeHost(
            providerAdapters: [],
            jobExecutors: [executor],
            runtimeRootDirectory: runtimeRoot,
            localDeviceId: LocalDeviceId);
        host.JobStatusChanged += status => statuses.Enqueue(status.Status);

        var requestId = "installed-update-" + Guid.NewGuid().ToString("N");
        var job = new ConnectorJobEnvelope(
            SchemaVersion: 1,
            RequestId: requestId,
            ModuleId: "installed-update-smoke",
            Provider: CadProvider.AutoCad,
            Operation: JobOperation.Healthcheck,
            CreatedAtUtc: DateTime.UtcNow,
            Payload: JsonSerializer.SerializeToElement(new { phase = "A" }),
            CorrelationId: "installed-update-smoke",
            ExecutorId: ExecutorId,
            Scope: ExecutionScope.DeviceLocal(ConnectorProductId.Platform));

        var completion = host.SubmitLocalAsync(job);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(statuses.Contains(JobStatus.Running), "The synthetic local job never reached Running.");

        var drain = host.RequestDrainAsync(TimeSpan.FromSeconds(15));
        await Task.Yield();
        var draining = host.DrainSnapshot;
        Require(!drain.IsCompleted, "Drain completed while the synthetic executor was blocked.");
        Require(draining.Phase == ConnectorDrainPhase.Draining && draining.ActiveOperations == 1,
            "Drain did not retain the active local execution.");
        Require(!executor.ExecutionCancellation.IsCancellationRequested,
            "Drain interrupted the active local execution.");

        executor.Release();
        var terminal = await completion.WaitAsync(TimeSpan.FromSeconds(10));
        var ready = await drain.WaitAsync(TimeSpan.FromSeconds(10));
        Require(terminal.Status == JobStatus.Success, "The synthetic local job did not complete successfully.");
        Require(ResultValue(terminal.Result) == ResultMarker, "The synthetic terminal result marker is missing.");
        Require(!executor.WasCanceled && executor.Calls == 1, "The synthetic executor was interrupted or rerun.");
        Require(ready.Phase == ConnectorDrainPhase.ReadyToApply && ready.ActiveOperations == 0,
            "Runtime did not reach ReadyToApply after the local execution completed.");

        var evidence = new ProbeEvidence(
            1,
            requestId,
            "A",
            statuses.Select(value => value.ToString()).ToArray(),
            draining.Phase.ToString(),
            draining.ActiveOperations,
            ready.Phase.ToString(),
            terminal.Status.ToString(),
            ResultMarker,
            NoInterruption: true,
            ImmutableHistoryRead: false,
            ExecutorRerunCount: 0,
            DpapiMarkerValidated: false);
        evidence = evidence with { PhaseAVersion = ExecutingVersion };
        await WriteEvidenceAsync(evidencePath, evidence);
    }

    private static async Task RunPhaseBAsync(
        SettingsService settings,
        string markerPath,
        string runtimeRoot,
        string evidencePath)
    {
        Require(File.Exists(markerPath), "The phase-A sentinel is missing.");
        Require(File.Exists(evidencePath), "The phase-A runtime evidence is missing.");
        var persistedSettings = settings.Load();
        Require(persistedSettings.DeviceId == "update-smoke-device", "The persisted device identity changed.");
        var marker = await File.ReadAllTextAsync(markerPath);
        Require(SettingsService.DecryptToken(persistedSettings.TokenCipherBase64) == marker,
            "The DPAPI-protected marker did not survive the update.");

        var phaseA = JsonSerializer.Deserialize<ProbeEvidence>(await File.ReadAllTextAsync(evidencePath), JsonOptions)
            ?? throw new InvalidDataException("The phase-A runtime evidence is empty.");
        Require(phaseA.SchemaVersion == 1 && phaseA.Phase == "A" && phaseA.TerminalStatus == JobStatus.Success.ToString() &&
                phaseA.ResultMarker == ResultMarker && phaseA.DrainBefore == ConnectorDrainPhase.Draining.ToString() &&
                phaseA.DrainAfter == ConnectorDrainPhase.ReadyToApply.ToString() && phaseA.NoInterruption,
            "The phase-A runtime evidence is invalid.");

        var executor = new RejectRerunExecutor();
        await using var host = new ConnectorRuntimeHost(
            providerAdapters: [],
            jobExecutors: [executor],
            runtimeRootDirectory: runtimeRoot,
            localDeviceId: LocalDeviceId);
        var history = await host.PrepareLocalAsync();
        var stored = history.Jobs.SingleOrDefault(job => job.Job.RequestId == phaseA.RequestId)
            ?? throw new InvalidDataException("The completed phase-A local job is absent from immutable history.");
        Require(stored.PersistedState is null && stored.LastStatus.Status == JobStatus.Success,
            "The phase-A history record is not terminal.");
        Require(ResultValue(stored.LastStatus.Result) == ResultMarker,
            "The immutable terminal result does not match phase A.");
        Require(executor.Calls == 0, "Reading immutable history reran the local executor.");

        await WriteEvidenceAsync(evidencePath, phaseA with
        {
            Phase = "B",
            ImmutableHistoryRead = true,
            ExecutorRerunCount = executor.Calls,
            DpapiMarkerValidated = true,
            PhaseBVersion = ExecutingVersion,
        });
    }

    private static string? ResultValue(JsonElement? result) =>
        result is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty("marker", out var marker) && marker.ValueKind == JsonValueKind.String
            ? marker.GetString()
            : null;

    private static async Task WriteEvidenceAsync(string path, ProbeEvidence evidence)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(evidence, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class BlockingExecutor : IConnectorJobExecutor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ExecutorId => Program.ExecutorId;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ExecutionCancellation { get; private set; }
        public int Calls { get; private set; }
        public bool WasCanceled { get; private set; }

        public async Task<ConnectorExecutionResult> ExecuteAsync(
            ConnectorJobEnvelope job,
            Func<ConnectorExecutionProgress, CancellationToken, Task>? progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            ExecutionCancellation = cancellationToken;
            Started.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCanceled = true;
                throw;
            }
            return new ConnectorExecutionResult(
                true,
                "Installed-update synthetic local execution completed.",
                Result: JsonSerializer.SerializeToElement(new { marker = ResultMarker }));
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class RejectRerunExecutor : IConnectorJobExecutor
    {
        public string ExecutorId => Program.ExecutorId;
        public int Calls { get; private set; }

        public Task<ConnectorExecutionResult> ExecuteAsync(
            ConnectorJobEnvelope job,
            Func<ConnectorExecutionProgress, CancellationToken, Task>? progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Immutable history read attempted to rerun the phase-A executor.");
        }
    }

    private sealed record ProbeEvidence(
        int SchemaVersion,
        string RequestId,
        string Phase,
        IReadOnlyList<string> ObservedStatuses,
        string DrainBefore,
        int ActiveOperationsBefore,
        string DrainAfter,
        string TerminalStatus,
        string ResultMarker,
        bool NoInterruption,
        bool ImmutableHistoryRead,
        int ExecutorRerunCount,
        bool DpapiMarkerValidated,
        string? PhaseAVersion = null,
        string? PhaseBVersion = null);
}
