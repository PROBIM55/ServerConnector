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
    private int _commonFolderCleanupPending;
    // A background invalidation belongs to the access session that observed
    // Ready.  This advances only after the lifecycle mutex has been acquired,
    // so a cancelled connect still leaves the active session intact.
    private readonly CommonConnectionActivation _commonActivation = new();
    private readonly CommonConnectionRecovery _commonRecovery = new();
    private int _disposed;
    public ConnectorRuntimeServices(string? runtimeRoot = null, string? ifcWorker = null, string? gltfpack = null)
    {
        var materializer = AgrConnectorIntegration.CreateInputMaterializer();
        var bundledWorker = Path.Combine(AppContext.BaseDirectory, "workers", "ifc-optimizer", "structura-ifc-optimizer.exe");
        var installedWorker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Structura IFC Optimizer", "engine", "structura-ifc-optimizer.exe");
        ifcWorker ??= File.Exists(bundledWorker) ? bundledWorker : installedWorker;
        var localRuntimeRoot = runtimeRoot ?? Path.Combine(UnifiedUserStatePaths.Root, "Agent");
        CommonAccess = CommonAccessRuntime.Create(localRuntimeRoot);
        CommonFolders = new CommonSmbAccessService(CommonAccess,
            () => CommonAccess.Current?.AccessProfile,
            CommonAccess.IsSmbDestinationAllowed,
            new WindowsSmbMappingPort(ProbeCommonOverlayAsync, CommonAccess.LegacyEncryptedSmbShares));
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
        var platformSettings = new PlatformSettingsPersistence(Path.Combine(localRuntimeRoot, "Platform"));
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
    public CommonAccessRuntime CommonAccess { get; }
    public CommonSmbAccessService CommonFolders { get; }
    public CommonConnectionOperations CommonOperations { get; } = new();
    public string CommonRecoveryProgress => _commonRecovery.Progress;
    public event Action? CommonRecoveryChanged { add => _commonRecovery.Changed += value; remove => _commonRecovery.Changed -= value; }
    public void CancelCommonConnectionIntent() => _commonRecovery.Stop();
    public string CommonFolderStatus => Volatile.Read(ref _commonFolderStatus);
    public bool CommonFolderCleanupPending => Volatile.Read(ref _commonFolderCleanupPending) != 0;

    private async ValueTask<NetworkOverlaySnapshot> ProbeCommonOverlayAsync(CancellationToken cancellationToken)
    {
        // Reuse the live connection coordinator. It checks the current server-issued
        // peer identity and NetBird CLI state without consuming an enrollment token.
        using var route = await CommonAccess.OpenProtectedServiceAsync(
            "control-plane", "jobs/health", cancellationToken).ConfigureAwait(false);
        return route.Overlay;
    }

    public Task ConnectCommonAsync(string? token, bool reconnect, CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        // Supersede automatic recovery synchronously, before waiting for its
        // lifecycle lock or any old CAD drain.
        var intentEpoch = _commonRecovery.BeginConnect();
        return ConnectCommonCoreAsync(token, reconnect, cancellationToken, progress, manualIntentEpoch: intentEpoch);
    }

    private async Task ConnectCommonCoreAsync(string? token, bool reconnect, CancellationToken cancellationToken,
        Action<string>? progress = null, CommonRecoveryAttempt? recovery = null, Action<string>? failure = null,
        long? manualIntentEpoch = null)
    {
        await _commonLifecycle.WaitAsync(cancellationToken);
        try
        {
            var intentEpoch = recovery?.Epoch ?? manualIntentEpoch ?? throw new InvalidOperationException("Connection intent missing.");
            if (!_commonRecovery.IsCurrent(intentEpoch)) throw new OperationCanceledException(cancellationToken);
            progress?.Invoke("Завершаем текущую работу…");
            await StopForConnectionChangeAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_commonRecovery.IsCurrent(intentEpoch)) throw new OperationCanceledException(cancellationToken);
            // Do not advance the ownership epoch until the old worker is
            // actually drained.  A cancelled queued/takeover request must
            // leave the still-running session able to process its own revoke.
            BeginCommonSession();
            try
            {
                await CommonFolders.ClearAuthorizationAsync(cancellationToken);
                // The old host is drained. A network change during the new
                // handshake must not strand/reuse its old Windows SMB sessions.
                // Exact non-force removal fails closed if files remain open.
                await CommonFolders.CleanupOwnedMappingsAfterDrainAsync(cancellationToken);
                Volatile.Write(ref _commonFolderCleanupPending, 0);
                SetCommonFolderStatus("Папки не подключены");
                await CommonAccess.ConnectAsync(token, reconnect, cancellationToken, progress);
                if (!CommonAccess.IsReady) return;
                var identityProfile = CommonAccess.Current!.AccessProfile!;
                CommonConnectionRecovery.RequireIdentity(recovery, new(
                    identityProfile.DeviceId, identityProfile.UserId, identityProfile.CompanyId));
                void ValidateCurrentIdentity()
                {
                    var current = CommonAccess.Current?.AccessProfile
                        ?? throw new InvalidOperationException("The recovery profile is unavailable.");
                    CommonConnectionRecovery.RequireIdentity(recovery, new(current.DeviceId, current.UserId, current.CompanyId));
                }
                progress?.Invoke("Проверяем защищённое соединение…");
                if (!await _commonActivation.ActivateAsync(CommonFolders,
                    async ct => { await ProbeCommonOverlayAsync(ct); },
                    () => CommonAccess.IsReady, cancellationToken, ValidateCurrentIdentity)) return;
                var profile = CommonAccess.Current!.AccessProfile!;
                CommonConnectionRecovery.RequireIdentity(recovery, new(profile.DeviceId, profile.UserId, profile.CompanyId));
                cancellationToken.ThrowIfCancellationRequested();
                if (!_commonRecovery.IsCurrent(intentEpoch)) throw new OperationCanceledException(cancellationToken);
                progress?.Invoke("Запускаем фоновую работу…");
                Host.Resume();
                await _commonRecovery.StartOwnedAsync(intentEpoch, () => Host.StartAsync(new ConnectorRuntimeOptions
                {
                    AuthenticationMode = ConnectorAuthenticationMode.IssuedCertificate,
                    DeviceId = profile.DeviceId,
                    ServerUrl = CommonAccess.ControlPlaneBaseUri.AbsoluteUri,
                    AgentType = "platform-cad-connector",
                    RunDemoJobWhenIdle = false,
                    EnableStatusShell = false,
                    Capabilities = ["autocad.build", "tekla.apply", "agr.publication", "converters.export"]
                }), cancellationToken);
                ValidateCurrentIdentity();
                _commonRecovery.MarkReady(intentEpoch, new(profile.DeviceId, profile.UserId, profile.CompanyId));
                if (profile.Modules.Any(module => module.ModuleId == "folders" &&
                    module.Permissions.Contains(Connector.Access.Contracts.ConnectorPermission.Read)))
                {
                    if (profile.Resources.Count == 0)
                    {
                        // The applied profile grants no resources. There is
                        // nothing to mount; do not repeat several live network
                        // checks just to obtain an empty folder receipt.
                        SetCommonFolderStatus("Папки не назначены");
                        return;
                    }
                    try
                    {
                        progress?.Invoke("Подключаем общие папки…");
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
            catch
            {
                failure?.Invoke(CommonAccess.Current?.DiagnosticCode ?? "connection_failed");
                // A completed handshake is not a completed native connection.
                // Close admission if host startup/folder setup is cancelled or
                // fails after Ready. Existing invalidation owns safe CAD drain;
                // never force-stop accepted work here or delete the credential.
                CommonFolders.InvalidateAuthorization();
                if (CommonAccess.IsReady)
                    await CommonAccess.DisconnectAsync(CancellationToken.None);
                throw;
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
        CancelCommonConnectionIntent();
        await _commonLifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopForConnectionChangeAsync(cancellationToken);
            BeginCommonSession();
            try
            {
                await CommonAccess.DisconnectAsync(cancellationToken);
            }
            finally
            {
                await CleanupCommonFoldersAfterDrainAsync();
                Host.Resume();
            }
        }
        finally { _commonLifecycle.Release(); }
    }

    public async Task SignOutCommonAsync(CancellationToken cancellationToken)
    {
        CancelCommonConnectionIntent();
        await _commonLifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopForConnectionChangeAsync(cancellationToken);
            BeginCommonSession();
            await CommonFolders.ClearAuthorizationAsync(cancellationToken);
            // A busy Windows handle must not prevent exact server revocation.
            // Keep its mapping pending, report it, and block later reuse until
            // the next drained exact cleanup can finish without force.
            await CleanupCommonFoldersAfterDrainAsync();
            await CommonAccess.SignOutAsync(cancellationToken);
        }
        finally { Host.Resume(); _commonLifecycle.Release(); }
    }

    private async Task CleanupCommonFoldersAfterDrainAsync()
    {
        try
        {
            await CommonFolders.CleanupOwnedMappingsAfterDrainAsync(CancellationToken.None);
            Volatile.Write(ref _commonFolderCleanupPending, 0);
            SetCommonFolderStatus("Папки не подключены");
        }
        catch
        {
            Volatile.Write(ref _commonFolderCleanupPending, 1);
            SetCommonFolderStatus("Некоторые папки заняты. Закройте открытые файлы и повторите отключение папок.");
        }
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
        _commonActivation.InvalidateAccess(invalidation.SessionGeneration, generation =>
            BeginCommonInvalidation(generation, CommonMappingCleanup.AllOwned, routeEpoch: null,
                "Папки не подключены", invalidation.Snapshot));
    }

    private void OnCommonRouteInvalidated(CommonSmbRouteInvalidation invalidation)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _commonActivation.InvalidateRoute(invalidation.RouteEpoch, generation =>
            BeginCommonInvalidation(generation, CommonMappingCleanup.UnsafeRoute,
                invalidation.RouteEpoch, "Доступ к папкам приостановлен после изменения сети",
                CommonAccess.Current is { } current ? current with { DiagnosticCode = "overlay_not_ready" } : null));
    }

    private void BeginCommonInvalidation(
        long invalidatedGeneration,
        CommonMappingCleanup mappingCleanup,
        long? routeEpoch,
        string status,
        CommonConnectorConnectionSnapshot? recoverySnapshot = null)
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
                drain, protectedDrain, invalidatedGeneration, mappingCleanup, routeEpoch, recoverySnapshot);
            _commonInvalidationTask = Task.WhenAll(_commonInvalidationTask, completion);
        }
    }

    private async Task CompleteCommonInvalidationAsync(
        Task<ConnectorDrainSnapshot> drainTask,
        IDisposable? protectedDrain,
        long invalidatedGeneration,
        CommonMappingCleanup mappingCleanup,
        long? routeEpoch,
        CommonConnectorConnectionSnapshot? recoverySnapshot)
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
        if (stopped && IsCurrentCommonSession(invalidatedGeneration) && recoverySnapshot is not null)
            _commonRecovery.Request(recoverySnapshot.DiagnosticCode, recoverySnapshot.DeviceId, ResumeCommonAfterLossAsync);
    }

    private async Task<CommonRecoveryResult> ResumeCommonAfterLossAsync(CommonRecoveryAttempt attempt)
    {
        while (CommonOperations.Progress.Operation is not null)
            await Task.Delay(100, attempt.Token).ConfigureAwait(false);
        var diagnostic = "connection_failed";
        try
        {
            // Resume may renew an expiring certificate using the existing
            // durable key. It cannot enroll or change device/user/company;
            // the exact identity fence in the core runs before activation.
            await ConnectCommonCoreAsync(null, true, attempt.Token, recovery: attempt,
                failure: code => diagnostic = code).ConfigureAwait(false);
            return new(CommonAccess.IsReady && Host.IsRunning, CommonAccess.Current?.DiagnosticCode ?? diagnostic);
        }
        catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested) { throw; }
        catch { return new(false, diagnostic); }
    }

    private bool IsCurrentCommonSession(long generation) =>
        _commonActivation.IsCurrent(generation);

    private void BeginCommonSession()
    {
        _commonActivation.BeginSession(CommonAccess.BeginSession);
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
        CancelCommonConnectionIntent();
        await _commonRecovery.DisposeAsync();
        await CommonOperations.DisposeAsync();
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
