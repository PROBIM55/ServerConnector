using System.Text.Json;
using System.IO;
using System.Diagnostics;
using Connector.AgrConversion.Service;
using Connector.Desktop.Features.ConverterWorkspace;
using Connector.JobModules;
using Connector.Network;
using Connector.SmbAccess;
using Connector.Platform;
using Platform.Cad.AutoCad.Adapter;
using Platform.Cad.Tekla.Adapter;
using Platform.Connector.Compute.Agr;
using Platform.Connector.Core;

namespace Connector.Desktop.Services;

/// <summary>Application composition: one Agent owns every local and remote executor.</summary>
public sealed class ConnectorRuntimeServices : IAsyncDisposable
{
    private readonly SemaphoreSlim _commonLifecycle = new(1, 1);
    private readonly object _commonInvalidationSync = new();
    private Task _commonInvalidationTask = Task.CompletedTask;
    private string _commonFolderStatus = "Папки не подключены";
    // A background invalidation belongs to the access session that observed
    // Ready.  This advances only after the lifecycle mutex has been acquired,
    // so a cancelled connect still leaves the active session intact.
    private long _commonSessionGeneration;
    private int _disposed;
    public ConnectorRuntimeServices(string? runtimeRoot = null, string? ifcWorker = null, string? gltfpack = null)
    {
        var materializer = AgrConnectorIntegration.CreateInputMaterializer();
        var bundledWorker = Path.Combine(AppContext.BaseDirectory, "workers", "ifc-optimizer", "structura-ifc-optimizer.exe");
        var installedWorker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Structura IFC Optimizer", "engine", "structura-ifc-optimizer.exe");
        ifcWorker ??= File.Exists(bundledWorker) ? bundledWorker : installedWorker;
        var localRuntimeRoot = runtimeRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Structura Connector", "Agent");
        CommonAccess = CommonAccessRuntime.Create(localRuntimeRoot);
        CommonFolders = new CommonSmbAccessService(CommonAccess,
            () => CommonAccess.Current?.AccessProfile,
            CommonAccess.IsSmbDestinationAllowed,
            new WindowsSmbMappingPort(ProbeCommonOverlayAsync));
        Host = new ConnectorRuntimeHost(
            [new AutoCadProviderAdapter(), new TeklaProviderAdapter()],
            [AgrConnectorIntegration.CreateExecutor(materializer),
                new FbxGlbJobExecutor(gltfpack ?? AgrGltfpackPartConverter.DefaultGltfpackPath),
                new IfcOptimizeJobExecutor(new IfcOptimizeOptions { ExecutablePath = ifcWorker }),
                new IfcOptimizeJobExecutor(new IfcOptimizeOptions { ExecutablePath = ifcWorker }, IfcOptimizeJobExecutor.AnalyzeId, "analyze")],
            runtimeRootDirectory: localRuntimeRoot,
            localDeviceId: LocalRuntimeIdentity.LoadOrCreate(localRuntimeRoot),
            moduleDescriptors:
            [
                new SharedModuleDescriptor("converters", "Конвертеры", [ConnectorProductId.Structura, ConnectorProductId.Platform],
                    [FbxGlbJobExecutor.Id, IfcOptimizeJobExecutor.Id, IfcOptimizeJobExecutor.AnalyzeId])
            ],
            controlPlaneClientFactory: _ => new HttpMtlsConnectorControlPlaneClient(CommonAccess,
                CommonAccess.Current?.AccessProfile?.CompanyId ?? throw new InvalidOperationException("Нет подтверждённого профиля доступа.")));
        var platformSettings = new PlatformSettingsPersistence(runtimeRoot is null
            ? null
            : Path.Combine(localRuntimeRoot, "Platform"));
        if (runtimeRoot is null)
        {
            // The production composition is the only path that may inspect the
            // previous Platform profile. Fixtures always stay within runtimeRoot.
            PlatformLegacyImport = platformSettings.ImportLegacyIfFresh();
            if (!PlatformLegacyImport.Imported)
            {
                Trace.TraceWarning("Platform legacy settings import did not complete: {0}",
                    PlatformLegacyImport.Code);
            }
        }
        Platform = new PlatformRuntimeFacade(Host, materializer, platformSettings);
        ConverterClient = new AgentConverterJobClient(Host);
        LegacyPartConverter = new AgentAgrPartConverter(ConverterClient);
        CommonAccess.ReadyInvalidated += OnCommonAccessInvalidated;
        CommonFolders.RouteInvalidated += OnCommonRouteInvalidated;
    }

    public ConnectorRuntimeHost Host { get; }
    public PlatformRuntimeFacade Platform { get; }
    public AgentConverterJobClient ConverterClient { get; }
    public IAgrPartConverter LegacyPartConverter { get; }
    /// <summary>First-run legacy import outcome; contains no credential data.</summary>
    public PlatformLegacyImportResult? PlatformLegacyImport { get; }
    public CommonAccessRuntime CommonAccess { get; }
    public CommonSmbAccessService CommonFolders { get; }
    public string CommonFolderStatus => Volatile.Read(ref _commonFolderStatus);

    private async ValueTask<NetworkOverlaySnapshot> ProbeCommonOverlayAsync(CancellationToken cancellationToken)
    {
        // Reuse the live connection coordinator. It checks the current server-issued
        // peer identity and NetBird CLI state without consuming an enrollment token.
        using var route = await CommonAccess.OpenProtectedServiceAsync(
            "control-plane", "jobs/health", cancellationToken).ConfigureAwait(false);
        return route.Overlay;
    }

    public async Task ConnectCommonAsync(string? token, bool reconnect, CancellationToken cancellationToken)
    {
        await _commonLifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopForConnectionChangeAsync(cancellationToken);
            // Do not advance the ownership epoch until the old worker is
            // actually drained.  A cancelled queued/takeover request must
            // leave the still-running session able to process its own revoke.
            BeginCommonSession();
            try
            {
                await CommonFolders.ClearAuthorizationAsync(cancellationToken);
                SetCommonFolderStatus("Папки не подключены");
                var routeEpoch = CommonFolders.RouteEpoch;
                await CommonAccess.ConnectAsync(token, reconnect, cancellationToken);
                if (!CommonAccess.IsReady) return;
                CommonFolders.EnableAuthorization(routeEpoch);
                var profile = CommonAccess.Current!.AccessProfile!;
                Host.Resume();
                await Host.StartAsync(new ConnectorRuntimeOptions
                {
                    AuthenticationMode = ConnectorAuthenticationMode.IssuedCertificate,
                    DeviceId = profile.DeviceId,
                    ServerUrl = CommonAccess.ControlPlaneBaseUri.AbsoluteUri,
                    AgentType = "platform-cad-connector",
                    RunDemoJobWhenIdle = false,
                    EnableStatusShell = false,
                    Capabilities = ["autocad.build", "tekla.apply", "agr.publication", "converters.export"]
                });
                if (profile.Modules.Any(module => module.ModuleId == "folders" &&
                    module.Permissions.Contains(Connector.Access.Contracts.ConnectorPermission.Read)))
                {
                    try
                    {
                        await CommonFolders.RefreshAndMountAsync(cancellationToken);
                        SetCommonFolderStatus(CommonAccess.IsReady
                            ? CommonFolders.Folders.Count == 0 ? "Папки не назначены" : "Папки подключены"
                            : "Папки не подключены");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception)
                    {
                        SetCommonFolderStatus(CommonAccess.IsReady
                            ? "Не удалось подключить папки. Попробуйте подключить диск вручную."
                            : "Папки не подключены");
                    }
                }
            }
            finally
            {
                // An invalidation callback may have closed the running host's
                // gate while this lifecycle operation was still finishing.
                if (!Host.IsRunning || CommonAccess.IsReady && CommonFolders.IsAuthorizationAdmissionOpen)
                    Host.Resume();
            }
        }
        finally { _commonLifecycle.Release(); }
    }

    public async Task DisconnectCommonAsync(CancellationToken cancellationToken)
    {
        await _commonLifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopForConnectionChangeAsync(cancellationToken);
            BeginCommonSession();
            try
            {
                await CommonAccess.DisconnectAsync(cancellationToken);
                await CommonFolders.ClearAuthorizationAsync(cancellationToken);
                SetCommonFolderStatus("Папки не подключены");
            }
            finally { Host.Resume(); }
        }
        finally { _commonLifecycle.Release(); }
    }

    private async Task StopForConnectionChangeAsync(CancellationToken cancellationToken)
    {
        // Finish accepted work before rotating its server session/credential.
        // A timeout leaves the current session intact and does not cancel CAD.
        try
        {
            var phase = Host.DrainSnapshot.Phase;
            if (phase == ConnectorDrainPhase.ReadyToApply)
            {
                await Host.StopAsync();
                return;
            }

            var drain = phase == ConnectorDrainPhase.Running
                ? await Host.RequestDrainAsync(TimeSpan.FromSeconds(60), cancellationToken)
                : await Host.RequestDrainOrJoinAsync(TimeSpan.FromSeconds(60), cancellationToken);
            if (drain.Phase != ConnectorDrainPhase.ReadyToApply)
                throw new InvalidOperationException("Дождитесь завершения выполняемого задания и повторите подключение.");
            await Host.StopAsync();
        }
        catch { Host.Resume(); throw; }
    }

    private void OnCommonAccessInvalidated(CommonAccessInvalidation invalidation)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var invalidatedGeneration = invalidation.SessionGeneration;
        if (!IsCurrentCommonSession(invalidatedGeneration)) return;

        BeginCommonInvalidation(invalidatedGeneration, CommonMappingCleanup.AllOwned, routeEpoch: null,
            "Папки не подключены");
    }

    private void OnCommonRouteInvalidated(CommonSmbRouteInvalidation invalidation)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var invalidatedGeneration = Volatile.Read(ref _commonSessionGeneration);
        if (invalidatedGeneration <= 0) return;
        BeginCommonInvalidation(invalidatedGeneration, CommonMappingCleanup.UnsafeRoute,
            invalidation.RouteEpoch, "Доступ к папкам приостановлен после изменения сети");
    }

    private void BeginCommonInvalidation(
        long invalidatedGeneration,
        CommonMappingCleanup mappingCleanup,
        long? routeEpoch,
        string status)
    {
        if (!IsCurrentCommonSession(invalidatedGeneration)) return;

        // These synchronous steps are the security boundary: no new SMB
        // credential request and no new remote job can enter after callback.
        CommonFolders.InvalidateAuthorization();
        SetCommonFolderStatus(status);

        IDisposable? protectedDrain = null;
        Task<ConnectorDrainSnapshot> drain;
        try
        {
            protectedDrain = Host.BeginProtectedDrain();
            drain = Host.RequestDrainOrJoinAsync(Timeout.InfiniteTimeSpan, CancellationToken.None);
        }
        catch
        {
            protectedDrain?.Dispose();
            drain = Task.FromResult(Host.DrainSnapshot); // Existing gate remains fail-closed.
        }

        lock (_commonInvalidationSync)
        {
            var completion = CompleteCommonInvalidationAsync(
                drain, protectedDrain, invalidatedGeneration, mappingCleanup, routeEpoch);
            _commonInvalidationTask = Task.WhenAll(_commonInvalidationTask, completion);
        }
    }

    private async Task CompleteCommonInvalidationAsync(
        Task<ConnectorDrainSnapshot> drainTask,
        IDisposable? protectedDrain,
        long invalidatedGeneration,
        CommonMappingCleanup mappingCleanup,
        long? routeEpoch)
    {
        await _commonLifecycle.WaitAsync().ConfigureAwait(false);
        var stopped = false;
        try
        {
            if (!IsCurrentCommonSession(invalidatedGeneration)) return;
            await CommonFolders.ClearAuthorizationAsync().ConfigureAwait(false);
            if (!IsCurrentCommonSession(invalidatedGeneration)) return;
            var drain = await drainTask.ConfigureAwait(false);
            if (IsCurrentCommonSession(invalidatedGeneration) && drain.Phase == ConnectorDrainPhase.ReadyToApply)
            {
                try
                {
                    if (mappingCleanup == CommonMappingCleanup.AllOwned)
                        await CommonFolders.CleanupOwnedMappingsAfterDrainAsync().ConfigureAwait(false);
                    else
                        await CommonFolders.CleanupUnsafeMappingsAfterDrainAsync(
                            routeEpoch ?? CommonFolders.RouteEpoch).ConfigureAwait(false);
                }
                catch
                {
                    SetCommonFolderStatus("Не удалось безопасно отключить папки. Закройте открытые файлы и подключитесь повторно");
                }
                await Host.StopAsync().ConfigureAwait(false);
                stopped = true;
            }
        }
        finally
        {
            // Resume only after the remote worker is stopped. This keeps local
            // submissions usable without reopening the old server session.
            protectedDrain?.Dispose();
            if (stopped && IsCurrentCommonSession(invalidatedGeneration)) Host.Resume();
            _commonLifecycle.Release();
        }
    }

    private bool IsCurrentCommonSession(long generation) =>
        Volatile.Read(ref _commonSessionGeneration) == generation;

    private void BeginCommonSession()
    {
        var generation = checked(Interlocked.Increment(ref _commonSessionGeneration));
        CommonAccess.BeginSession(generation);
    }

    private void SetCommonFolderStatus(string value) => Volatile.Write(ref _commonFolderStatus, value);

    private enum CommonMappingCleanup
    {
        AllOwned,
        UnsafeRoute,
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CommonAccess.ReadyInvalidated -= OnCommonAccessInvalidated;
        CommonFolders.RouteInvalidated -= OnCommonRouteInvalidated;
        CommonFolders.InvalidateAuthorization();
        ConverterClient.Dispose();
        await Platform.DisposeAsync();
        await Host.DisposeAsync();
        Task invalidation;
        lock (_commonInvalidationSync) invalidation = _commonInvalidationTask;
        try { await invalidation.ConfigureAwait(false); }
        catch { }
        await CommonAccess.DisconnectAsync(CancellationToken.None);
        await CommonFolders.ClearAuthorizationAsync();
        CommonFolders.Dispose();
    }
}

public sealed class AgentConverterJobClient : IConverterJobClient, IDisposable
{
    private readonly ConnectorRuntimeHost _host;
    public AgentConverterJobClient(ConnectorRuntimeHost host)
    {
        _host = host;
        _host.JobStatusChanged += Forward;
    }
    public event Action<ConnectorJobStatusEnvelope>? StatusChanged;
    private void Forward(ConnectorJobStatusEnvelope status) => StatusChanged?.Invoke(status);

    public Task<ConnectorJobStatusEnvelope> SubmitAsync(string requestId, string executorId, string inputPath,
        string outputDirectory, string profile, CancellationToken cancellationToken)
        => _host.SubmitLocalAsync(new ConnectorJobEnvelope(1, requestId, "converters", CadProvider.Tekla,
            JobOperation.Export, DateTime.UtcNow, JsonSerializer.SerializeToElement(new { inputPath, outputDirectory, profile }),
            ExecutorId: executorId, Scope: ExecutionScope.DeviceLocal(ConnectorProductId.Structura)), cancellationToken);

    public Task<ConnectorJobStatusEnvelope> SubmitAsync(ConverterJobRequest request, CancellationToken cancellationToken)
        => _host.SubmitLocalAsync(new ConnectorJobEnvelope(1, request.RequestId, "converters", CadProvider.Tekla,
            JobOperation.Export, DateTime.UtcNow,
            JsonSerializer.SerializeToElement(new { inputPath = request.InputPath, outputDirectory = request.OutputDirectory, profile = request.Profile }),
            ExecutorId: request.ExecutorId, Scope: ExecutionScope.DeviceLocal(request.Product)), cancellationToken);

    public void Dispose() => _host.JobStatusChanged -= Forward;
}
