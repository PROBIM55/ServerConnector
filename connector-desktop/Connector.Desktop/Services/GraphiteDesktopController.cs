using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;
using Connector.Access.Contracts;
using Connector.Desktop.Features.Connector;
using Connector.Desktop.Features.ConverterWorkspace;
using Connector.Desktop.Features.GraphiteWeb;
using Connector.Desktop.Features.PlatformTools;
using Connector.Desktop.Features.Structura;
using Connector.Desktop.Features.Tekla.ModelSharing;
using Connector.Desktop.Features.Tekla.Patching;
using Connector.Desktop.Features.Vpn;
using Connector.Desktop.Mvvm;
using Connector.Desktop.Shell;
using Connector.Platform;
using Connector.SmbAccess;
using Platform.Connector.Core;

namespace Connector.Desktop.Services;

/// <summary>
/// Allowlisted native command boundary for the local Graphite document. Paths accepted by jobs originate only
/// from Windows pickers and remain native; snapshots expose file names, sizes and result file names only.
/// </summary>
public sealed class GraphiteDesktopController : IAsyncDisposable
{
    private static readonly string ApplicationVersion = ResolveApplicationVersion();
    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "structura", "platform" };
    private static readonly HashSet<string> Converters = new(StringComparer.Ordinal) { "catalog", "fbx", "ifc", "history" };
    private static readonly HashSet<string> TeklaPages = new(StringComparer.Ordinal) { "standard", "sharing", "ifc", "exports" };
    private static readonly HashSet<string> Pages = new(StringComparer.Ordinal)
    {
        "overview", "services", "tekla", "attributes", "folders", "converters", "agr", "bridge",
        "autocad", "jobs", "settings", "admin"
    };

    private readonly object _gate = new();
    private readonly IConnectorHost _connectorHost;
    private readonly ConnectorRuntimeServices _runtimeServices;
    private readonly PlatformToolsViewModel _platformTools;
    private readonly IPlatformConnectionApi _platformConnection;
    private readonly CommonAccessRuntime _commonAccess;
    private readonly Func<string, Task<bool>>? _confirmFolderDisconnect;
    private readonly ConverterWorkspaceViewModel _converterWorkspace;
    private readonly Func<string, string?>? _pickPath;
    private readonly Func<string, string[]?>? _pickFiles;
    private readonly PatchingViewModel? _patching;
    private readonly ModelSharingViewModel? _modelSharing;
    private readonly VpnViewModel? _vpn;
    private readonly StructuraViewModel? _structura;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _pending = new();
    private readonly Dictionary<string, object> _fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _savedFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pickedFolders = new(StringComparer.Ordinal);
    private readonly List<NativeFile> _files = new();
    private readonly List<NativeFile> _ifcFiles = new();
    private string _mode = "structura";
    private string _page = "overview";
    private string _converter = "catalog";
    private string _tekla = "standard";
    private SafeNotice? _notice;
    private bool _firstSnapshot = true;
    private bool _disposed;
    private string? _converterSubmissionMode;
    private IReadOnlyList<PlatformAutoCadSession> _cadSessions = Array.Empty<PlatformAutoCadSession>();

    public GraphiteDesktopController(
        IConnectorHost connectorHost,
        ConnectorRuntimeServices runtimeServices,
        ShellViewModel shell,
        PlatformToolsViewModel platformTools,
        ConverterWorkspaceViewModel converterWorkspace,
        Func<string, string?>? pickPath = null,
        Func<string, string[]?>? pickFiles = null,
        IPlatformConnectionApi? platformConnection = null,
        Func<string, Task<bool>>? confirmFolderDisconnect = null)
    {
        _connectorHost = connectorHost ?? throw new ArgumentNullException(nameof(connectorHost));
        _runtimeServices = runtimeServices ?? throw new ArgumentNullException(nameof(runtimeServices));
        ArgumentNullException.ThrowIfNull(shell);
        _platformTools = platformTools ?? throw new ArgumentNullException(nameof(platformTools));
        _platformConnection = platformConnection ?? runtimeServices.Platform;
        _commonAccess = runtimeServices.CommonAccess;
        _confirmFolderDisconnect = confirmFolderDisconnect;
        _runtimeServices.Platform.SnapshotChanged += OnPlatformSnapshotChanged;
        _converterWorkspace = converterWorkspace ?? throw new ArgumentNullException(nameof(converterWorkspace));
        if (Path.IsPathFullyQualified(_connectorHost.Settings.ConverterOutputDirectory))
            _converterWorkspace.OutputDirectory = _connectorHost.Settings.ConverterOutputDirectory;
        _fields["output-root"] = new DirectoryInfo(_converterWorkspace.OutputDirectory).Name;
        _fields["ifc-output"] = new DirectoryInfo(_converterWorkspace.OutputDirectory).Name;
        _pickPath = pickPath;
        _pickFiles = pickFiles;

        _patching = ViewModel<PatchingViewModel>(shell.Tekla, "Патчинг");
        _modelSharing = ViewModel<ModelSharingViewModel>(shell.Tekla, "Model Sharing");
        _vpn = ViewModel<VpnViewModel>(shell.Vpn, "Общая папка (VPN)");
        _structura = ViewModel<StructuraViewModel>(shell.Structura, "Structura");
        RestoreSettingsFields();

        Subscribe(_platformTools);
        Subscribe(_converterWorkspace);
        Subscribe(_patching);
        Subscribe(_modelSharing);
        Subscribe(_vpn);
        Subscribe(_structura);
        _converterWorkspace.Jobs.CollectionChanged += OnJobsChanged;
        foreach (ConverterWorkspaceJob job in _converterWorkspace.Jobs) Subscribe(job);
    }

    public event Action<object>? SnapshotChanged;

    public object CreateSnapshot() => CreateSnapshot(includeFields: false);

    public object CreateDocumentSnapshot()
    {
        RestoreSettingsFields();
        return CreateSnapshot(includeFields: false, includeSavedFields: true);
    }

    private void RestoreSettingsFields()
    {
        var settings = _connectorHost.Settings;
        lock (_gate)
        {
            RestoreFolder("xs-path", settings.TeklaStandardLocalPath);
            RestoreFolder("ext-path", settings.TeklaExtensionsLocalPath);
            RestoreFolder("lib-path", settings.TeklaLibrariesLocalPath);
            RestoreFolder("publish-firm-path", settings.TeklaPublishSourcePath);
            RestoreFolder("publish-ext-path", settings.TeklaExtensionsPublishSourcePath);
            RestoreFolder("publish-lib-path", settings.TeklaLibrariesPublishSourcePath);
            RestoreFolder("sharing-bin", _modelSharing?.TeklaBin ?? settings.ModelSharingTeklaBin);
            RestoreFolder("ifc-bin", _patching?.TeklaBin ?? settings.IfcPatchingTeklaBin);
            RestoreFolder("patch-folder", _patching?.StagingDir ?? settings.IfcPatchingStagingDir);
            RestoreFolder("output-root", _converterWorkspace.OutputDirectory);
            RestoreFolder("ifc-output", _converterWorkspace.OutputDirectory);
            _savedFields["autostart"] = settings.AutoStart;
            _savedFields["heartbeat-seconds"] = settings.HeartbeatSeconds;
        }
    }

    private void RestoreFolder(string name, string? path)
    {
        // Display only the folder name. Native view models retain the complete
        // path, including unavailable/network folders; restoration performs no I/O.
        _savedFields[name] = Path.GetFileName((_pickedFolders.GetValueOrDefault(name) ?? path ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private object CreateSnapshot(bool includeFields, bool includeSavedFields = false)
    {
        bool reset;
        string mode;
        string page;
        string converter;
        string tekla;
        Dictionary<string, object> fields;
        Dictionary<string, object>? savedFields;
        NativeFile[] files;
        NativeFile[] ifcFiles;
        SafeNotice? notice;
        lock (_gate)
        {
            reset = _firstSnapshot;
            _firstSnapshot = false;
            mode = _mode;
            page = _page;
            converter = _converter;
            tekla = _tekla;
            fields = includeFields || reset
                ? new Dictionary<string, object>(_fields, StringComparer.Ordinal)
                : new Dictionary<string, object>(StringComparer.Ordinal);
            savedFields = includeSavedFields ? new Dictionary<string, object>(_savedFields, StringComparer.Ordinal) : null;
            files = _files.ToArray();
            ifcFiles = _ifcFiles.ToArray();
            notice = _notice;
        }

        object[] history = _converterWorkspace.Jobs.Select(JobSnapshot).ToArray();
        ConverterWorkspaceJob? current = _converterWorkspace.Jobs.FirstOrDefault(job => job.State == "Выполняется")
            ?? _converterWorkspace.SelectedJob ?? _converterWorkspace.Jobs.FirstOrDefault();
        object? job = current is null ? null : JobSnapshot(current);
        var moduleFields = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["agent-status"] = mode == "platform"
                ? PlatformStatus()
                : _commonAccess.IsSelected ? _commonAccess.DisplayStatus : _connectorHost.IsConnected ? "Работает" : "Остановлен",
            ["agr-status"] = _platformTools.AgrStatus,
            ["bridge-status"] = _platformTools.TeklaStatus,
            ["cad-status"] = _platformTools.AutoCadStatus,
            ["sharing-status"] = SafeModelSharingStatus(),
            ["ifc-patch-status"] = SafePatchStatus(),
            ["vpn-status"] = _commonAccess.IsSelected ? _commonAccess.DisplayStatus : SafeVpnStatus(),
            ["cloud-status"] = _structura?.AccessStatusLine ?? "Недоступно",
            ["app-version"] = ApplicationVersion,
            ["firm-version"] = _connectorHost.Settings.TeklaStandardInstalledVersion ?? "Не синхронизировано",
            ["extensions-version"] = _connectorHost.Settings.TeklaExtensionsInstalledVersion ?? "Не синхронизировано",
            ["libraries-version"] = _connectorHost.Settings.TeklaLibrariesInstalledVersion ?? "Не синхронизировано"
        };

        var snapshot = new Dictionary<string, object?>
        {
            ["reset"] = reset,
            ["fields"] = fields,
            ["savedFields"] = savedFields,
            ["fieldMode"] = mode,
            ["moduleFields"] = moduleFields,
            ["files"] = files.Select(SafeFile).ToArray(),
            ["ifcFiles"] = ifcFiles.Select(file => SafeFile(file, CurrentFileState(file))).ToArray(),
            ["modelFolders"] = Array.Empty<string>(),
            ["commonAccessSelected"] = _commonAccess.IsSelected,
            ["assignedFolders"] = _commonAccess.IsSelected && _commonAccess.HasPermission(mode, "folders", ConnectorPermission.Read)
                ? _runtimeServices.CommonFolders.Folders : Array.Empty<CommonSmbFolder>(),
            ["folderStatus"] = _runtimeServices.CommonFolderStatus,
            ["jobs"] = history,
            ["history"] = history,
            ["availability"] = new
            {
                agentControls = _connectorHost.CanControlBackgroundConnection,
                publishTekla = !_commonAccess.IsSelected && _connectorHost.CanPublishTekla
            },
            ["allowedPages"] = _commonAccess.IsSelected ? new
            {
                structura = _commonAccess.AvailablePages("structura"),
                platform = _commonAccess.AvailablePages("platform")
            } : null,
            ["updateAvailable"] = _connectorHost.HasPendingUpdate,
            ["agrInstalling"] = _platformTools.IsBusy && _platformTools.InstallAgrCommand.IsRunning,
            ["status"] = _commonAccess.IsSelected ? _commonAccess.DisplayStatus : _connectorHost.IsConnected ? "Подключено" : "Не подключено",
            ["hasSavedCredential"] = _commonAccess.IsSelected ? _commonAccess.HasStoredCredential : !string.IsNullOrWhiteSpace(_connectorHost.Settings.TokenCipherBase64),
            ["connections"] = new
            {
                structura = new { status = _commonAccess.IsSelected ? _commonAccess.DisplayStatus : _connectorHost.IsConnected ? "Подключено" : "Не подключено",
                    hasSavedCredential = _commonAccess.IsSelected ? _commonAccess.HasStoredCredential : !string.IsNullOrWhiteSpace(_connectorHost.Settings.TokenCipherBase64) },
                platform = new { status = PlatformStatus(), hasSavedCredential = _commonAccess.IsSelected ? _commonAccess.HasStoredCredential : _platformConnection.Connection.HasStoredCredential }
            },
            ["cadSessions"] = _cadSessions,
            ["job"] = job,
            ["notice"] = notice
        };
        return snapshot;
    }

    public Task<object?> HandleAsync(GraphiteRpcRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ApplyRequestContext(request.Payload);

        if (_commonAccess.IsSelected)
        {
            if (IsLegacyProtectedCommand(request.Command))
                return Availability(false, "Этот раздел ожидает настройки доступа администратором.");
            var module = CommandModule(request.Command);
            var permission = request.Command is "models-mount" or "models-unmount" or "models-open" or "vpn-check"
                ? ConnectorPermission.Read : ConnectorPermission.Execute;
            if (module is not null && !_commonAccess.HasPermission(_mode, module, permission))
                return Availability(false, "Для этого действия нет доступа. Обратитесь к администратору.");
        }

        switch (request.Command)
        {
            case "connect":
                if (_commonAccess.ShouldUse(ReadField(request.Payload, "token"))) StartCommonConnect(ReadField(request.Payload, "token"), false, cancellationToken);
                else if (_mode == "platform") StartPlatformConnect(ReadField(request.Payload, "token"), reconnect: false, cancellationToken);
                else StartConnect(ReadField(request.Payload, "token"), cancellationToken);
                break;
            case "reconnect":
                if (_commonAccess.ShouldUse()) StartCommonConnect(null, true, cancellationToken);
                else if (_mode == "platform") StartPlatformConnect(null, reconnect: true, cancellationToken);
                else StartConnect(ReadStoredToken(), cancellationToken);
                break;
            case "disconnect":
                if (_commonAccess.ShouldUse()) StartOperation(() => _runtimeServices.DisconnectCommonAsync(cancellationToken), request.Command, cancellationToken);
                else if (_mode == "platform") StartOperation(async () => RequireAccepted(await _platformConnection.DisconnectAsync(cancellationToken)), request.Command, cancellationToken);
                else StartOperation(_connectorHost.DisconnectAsync, request.Command, cancellationToken);
                break;
            case "update-check":
                StartOperation(() => _connectorHost.CheckUpdatesAsync(showDialogs: true), request.Command, cancellationToken);
                break;
            case "update-download":
                StartSupportAction(DesktopSupportAction.DownloadPendingUpdate, request.Command, cancellationToken);
                break;
            case "release-notes":
                StartSupportAction(DesktopSupportAction.ShowReleaseNotes, request.Command, cancellationToken);
                break;
            case "settings-log":
            case "cad-log":
            case "bridge-log":
            case "agr-log":
            case "firm-log":
            case "xs-log":
            case "ext-log":
            case "lib-log":
                StartSupportAction(DesktopSupportAction.OpenApplicationJournal, request.Command, cancellationToken);
                break;
            case "log-clear":
                StartSupportAction(DesktopSupportAction.ClearApplicationJournal, request.Command, cancellationToken);
                break;
            case "diagnostics":
                StartSupportAction(DesktopSupportAction.ExportDiagnostics, request.Command, cancellationToken);
                break;
            case "log-folder":
                return Availability(false, "Журнал доступен кнопкой «Открыть журнал».");
            case "update-apply":
                StartOperation(() => _connectorHost.InstallPendingUpdateAsync(confirmBeforeRun: true), request.Command, cancellationToken);
                break;
            case "firm-sync-all":
                StartOperation(_connectorHost.RunTeklaInteractiveSyncAsync, request.Command, cancellationToken);
                break;
            case "xs-sync":
            case "ext-sync":
            case "lib-sync":
                StartSupportAction(request.Command switch
                {
                    "xs-sync" => DesktopSupportAction.SyncTeklaFirm,
                    "ext-sync" => DesktopSupportAction.SyncTeklaExtensions,
                    _ => DesktopSupportAction.SyncTeklaLibraries
                }, request.Command, cancellationToken);
                break;
            case "pick-input":
                PickInput(ReadString(request.Payload, "target"));
                break;
            case "choose-folder":
                PickFolder(ReadString(request.Payload, "target"));
                StartOperation(PersistSelectedFoldersAsync, request.Command, cancellationToken);
                break;
            case "files-clear":
                lock (_gate) _files.Clear();
                break;
            case "ifc-remove":
                RemoveIfc(ReadIndex(request.Payload));
                break;
            case "ifc-clear-finished":
                ClearFinishedIfc();
                break;
            case "start":
            case "ifc-optimize":
                StartConverter(request.Command, cancellationToken);
                break;
            case "cancel":
                Execute(_converterWorkspace.CancelCommand);
                break;
            case "results":
                SelectRequestedJob(request.Payload);
                Execute(_converterWorkspace.OpenResultCommand);
                break;
            case "report":
                SelectRequestedJob(request.Payload);
                Execute(_converterWorkspace.OpenReportCommand);
                break;
            case "history-clear":
                ClearHistory();
                break;
            case "jobs-refresh":
                break;
            case "settings-store":
                StartSavePreferences(request.Payload, reconnect: false, request.Command, cancellationToken);
                break;
            case "settings-save":
                StartSavePreferences(request.Payload, reconnect: true, request.Command, cancellationToken);
                break;
            case "ifc-detect":
                StartAsyncCommand(Require(_patching, "Патчинг IFC").DetectCommand, request.Command, cancellationToken);
                break;
            case "ifc-apply":
                ApplyPatchPickerPaths();
                StartAsyncCommand(Require(_patching, "Патчинг IFC").ApplyCommand, request.Command, cancellationToken);
                break;
            case "ifc-rollback":
                ApplyPatchPickerPaths();
                StartAsyncCommand(Require(_patching, "Патчинг IFC").RollbackCommand, request.Command, cancellationToken);
                break;
            case "ifc-log":
                Execute(Require(_patching, "Патчинг IFC").OpenLogCommand);
                break;
            case "sharing-check":
                Require(_modelSharing, "Model Sharing").RefreshStatus();
                break;
            case "sharing-config":
                ApplySharingPickerPath();
                StartAsyncCommand(Require(_modelSharing, "Model Sharing").SetupCommand, request.Command, cancellationToken);
                break;
            case "sharing-log":
                Execute(Require(_modelSharing, "Model Sharing").OpenLogCommand);
                break;
            case "vpn-enable":
                StartAsyncCommand(Require(_vpn, "VPN").EnableCommand, request.Command, cancellationToken);
                break;
            case "models-mount":
                if (_commonAccess.IsSelected)
                {
                    var resource = SelectedFolderResource();
                    var drive = ReadSafeField("drive");
                    StartOperation(() => _runtimeServices.CommonFolders.MountAsync(resource, drive, cancellationToken), request.Command, cancellationToken);
                }
                else StartOperation(() => _connectorHost.MountVpnShareAsync(ReadSafeField("drive")), request.Command, cancellationToken);
                break;
            case "models-unmount":
                if (_commonAccess.IsSelected)
                {
                    var resource = SelectedFolderResource();
                    var drive = _runtimeServices.CommonFolders.Folders.FirstOrDefault(folder => folder.ResourceId == resource)?.Drive ?? ReadSafeField("drive");
                    StartOperation(() => _runtimeServices.CommonFolders.UnmountAsync(resource, drive,
                        () => _confirmFolderDisconnect?.Invoke(drive) ?? Task.FromResult(false), cancellationToken), request.Command, cancellationToken);
                }
                else StartOperation(() => _connectorHost.UnmountVpnShareAsync(ReadSafeField("drive")), request.Command, cancellationToken);
                break;
            case "vpn-disable":
                StartAsyncCommand(Require(_vpn, "VPN").DisableCommand, request.Command, cancellationToken);
                break;
            case "vpn-check":
                if (_commonAccess.IsSelected)
                    StartOperation(async () =>
                    {
                        using var response = await _commonAccess.SendAsync(System.Net.Http.HttpMethod.Head, "jobs/health", null,
                            new Dictionary<string, string>(), cancellationToken);
                        if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
                            throw new InvalidOperationException("Не удалось подтвердить защищённое подключение.");
                    }, request.Command, cancellationToken);
                else Require(_vpn, "VPN").RefreshStatus();
                break;
            case "vpn-log":
                Execute(Require(_vpn, "VPN").OpenLogCommand);
                break;
            case "models-open":
                if (_commonAccess.IsSelected)
                {
                    var resource = SelectedFolderResource();
                    StartOperation(async () =>
                    {
                        var folder = await _runtimeServices.CommonFolders.GetFolderToOpenAsync(resource, cancellationToken);
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
                    }, request.Command, cancellationToken);
                }
                else Execute(Require(_vpn, "VPN").OpenFolderCommand);
                break;
            case "speckle-open":
                Execute(Require(_structura, "Structura").OpenSpeckleCommand);
                break;
            case "cloud-open":
                Execute(Require(_structura, "Structura").OpenNextcloudCommand);
                break;
            case "speckle-access":
                Execute(Require(_structura, "Structura").ShowSpeckleAccessCommand);
                break;
            case "cloud-access":
                Execute(Require(_structura, "Structura").ShowNextcloudAccessCommand);
                break;
            case "agr-probe":
                StartAsyncCommand(_platformTools.ProbeAgrCommand, request.Command, cancellationToken);
                break;
            case "agr-install":
                StartAsyncCommand(_platformTools.InstallAgrCommand, request.Command, cancellationToken);
                break;
            case "agr-install-cancel":
                Execute(_platformTools.CancelCommand);
                break;
            case "bridge-check":
                StartAsyncCommand(_platformTools.ProbeTeklaCommand, request.Command, cancellationToken);
                break;
            case "bridge-start":
            case "bridge-restart":
                StartOperation(async () => { await _platformTools.EnsureTeklaBridgeAsync(request.Command == "bridge-restart", cancellationToken); }, request.Command, cancellationToken);
                break;
            case "cad-refresh":
                StartOperation(async () =>
                {
                    var result = await _platformTools.RefreshAutoCadSessionsAsync(cancellationToken);
                    _cadSessions = result.Value ?? Array.Empty<PlatformAutoCadSession>();
                }, request.Command, cancellationToken);
                break;
            case "cad-ping":
                StartOperation(async () => { await _platformTools.SelectAndPingAutoCadAsync(ReadSafeField("cad-session"), cancellationToken); }, request.Command, cancellationToken);
                break;
            case "ifc-analyze":
                StartConverter(request.Command, cancellationToken);
                break;
            case "ifc-validation":
                SelectRequestedJob(request.Payload);
                Execute(_converterWorkspace.OpenValidationReportCommand);
                break;
            case "publish-validate":
                StartOperation(() => _connectorHost.ValidateTeklaPublicationAsync(CreateTeklaPublicationRequest(request.Payload), cancellationToken), request.Command, cancellationToken);
                break;
            case "publish-start":
                StartOperation(() => _connectorHost.PublishTeklaAsync(CreateTeklaPublicationRequest(request.Payload), cancellationToken), request.Command, cancellationToken);
                break;
            case "agent-start":
                StartBackgroundConnection(_mode, request.Command, cancellationToken);
                break;
            case "agent-stop":
                StopBackgroundConnection(_mode, request.Command, cancellationToken);
                break;
            case "exports-save":
            case "exports-run":
            case "exports-toggle":
            case "exports-history":
                return Availability(false, "Автоматические выгрузки моделей ещё разрабатываются.");
            default:
                return Availability(false, "Действие пока недоступно в этой версии.");
        }

        PublishSnapshot();
        return Task.FromResult<object?>(new { snapshot = CreateSnapshot(includeFields: true) });
    }

    private Task<object?> Availability(bool available, string message)
    {
        object snapshot = CreateSnapshot(includeFields: true);
        return Task.FromResult<object?>(new { available, message, snapshot });
    }

    private void StartSupportAction(DesktopSupportAction action, string command, CancellationToken cancellationToken) =>
        StartOperation(() => _connectorHost.ExecuteSupportActionAsync(action, cancellationToken), command, cancellationToken);

    private void StartConnect(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096)
            throw new InvalidOperationException("Нужен действующий токен устройства.");

        StartOperation(async () =>
        {
            await _connectorHost.ConnectByTokenAsync(token, showSuccessDialog: true);
        }, "connect", cancellationToken);
    }

    private void StartBackgroundConnection(string mode, string command, CancellationToken cancellationToken)
    {
        if (_commonAccess.IsSelected)
        {
            StartCommonConnect(null, true, cancellationToken);
            return;
        }
        if (!_connectorHost.CanControlBackgroundConnection)
            throw new InvalidOperationException("Управление фоновым подключением недоступно.");
        if (mode == "platform")
        {
            StartPlatformConnect(null, reconnect: true, cancellationToken);
            return;
        }
        StartOperation(() => _connectorHost.StartBackgroundConnectionAsync(cancellationToken), command, cancellationToken);
    }

    private void StopBackgroundConnection(string mode, string command, CancellationToken cancellationToken)
    {
        if (_commonAccess.IsSelected)
        {
            StartOperation(() => _runtimeServices.DisconnectCommonAsync(cancellationToken), command, cancellationToken);
            return;
        }
        if (!_connectorHost.CanControlBackgroundConnection)
            throw new InvalidOperationException("Управление фоновым подключением недоступно.");
        if (mode == "platform")
        {
            StartOperation(async () => RequireAccepted(await _platformConnection.DisconnectAsync(cancellationToken)), command, cancellationToken);
            return;
        }
        StartOperation(() => _connectorHost.StopBackgroundConnectionAsync(cancellationToken), command, cancellationToken);
    }

    private void StartSavePreferences(JsonElement payload, bool reconnect, string command, CancellationToken cancellationToken)
    {
        string mode = _mode;
        bool autoStart = ReadBooleanField(payload, "autostart", _connectorHost.Settings.AutoStart);
        int heartbeatSeconds = Math.Clamp(
            ReadIntField(payload, "heartbeat-seconds", _connectorHost.Settings.HeartbeatSeconds), 5, 3600);
        StartOperation(async () =>
        {
            await _connectorHost.SaveDesktopPreferencesAsync(CreatePreferences(autoStart, heartbeatSeconds));
            if (reconnect)
            {
                if (_commonAccess.IsSelected)
                    await _runtimeServices.ConnectCommonAsync(null, true, cancellationToken);
                else if (mode == "platform")
                {
                    await _connectorHost.EnsureVpnReadyAsync();
                    RequireAccepted(await _platformConnection.ReconnectAsync(cancellationToken));
                }
                else await _connectorHost.ConnectByTokenAsync(ReadStoredToken(), showSuccessDialog: true);
            }
        }, command, cancellationToken);
    }

    private string ReadStoredToken()
    {
        string token = SettingsService.DecryptToken(_connectorHost.Settings.TokenCipherBase64);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Сохранённый токен отсутствует.");
        return token;
    }

    private string PlatformStatus()
    {
        if (_commonAccess.IsSelected) return _commonAccess.DisplayStatus;
        var connection = _platformConnection.Connection;
        return connection.IsConnected ? "Подключено" : connection.IsBusy ? "Подключение…" :
            connection.Phase == ConnectorRuntimePhase.TokenInvalid ? "Токен отклонён" : "Не подключено";
    }

    private void StartPlatformConnect(string? token, bool reconnect, CancellationToken cancellationToken)
    {
        StartOperation(async () =>
        {
            if (!_connectorHost.Settings.VpnEnabled || string.IsNullOrWhiteSpace(_connectorHost.Settings.VpnConfigCipherBase64))
                await _connectorHost.ConnectByTokenAsync(ReadStoredToken(), showSuccessDialog: false);
            await _connectorHost.EnsureVpnReadyAsync();
            if (string.IsNullOrWhiteSpace(token) && !_platformConnection.Connection.HasStoredCredential)
                RequireAccepted(_platformConnection.ImportLegacyCredential());
            RequireAccepted(reconnect
                ? await _platformConnection.ReconnectAsync(cancellationToken)
                : await _platformConnection.ConnectAsync(token, cancellationToken));
        }, "connect-platform", cancellationToken);
    }

    private void StartCommonConnect(string? token, bool reconnect, CancellationToken cancellationToken)
        => StartOperation(async () =>
        {
            _commonAccess.Select();
            if (_connectorHost.IsConnected) await _connectorHost.DisconnectAsync();
            await _runtimeServices.ConnectCommonAsync(token, reconnect, cancellationToken);
        }, "connect-common", cancellationToken);

    // New enrollment cannot reuse legacy web passwords, SMB credentials or
    // direct HTTP transports. Those adapters require typed resource grants.
    private static bool IsLegacyProtectedCommand(string command) => command is
        "firm-sync-all" or "xs-sync" or "ext-sync" or "lib-sync" or
        "speckle-open" or "speckle-access" or "cloud-open" or "cloud-access" or
        "vpn-enable" or "vpn-disable" or "publish-start" or "publish-validate";

    private static string? CommandModule(string command) => command switch
    {
        "start" or "ifc-optimize" or "ifc-analyze" => "converters",
        "ifc-detect" or "ifc-apply" or "ifc-rollback" or "sharing-config" => "tekla",
        "agr-probe" or "agr-install" => "agr",
        "bridge-check" or "bridge-start" or "bridge-restart" => "tekla",
        "cad-refresh" or "cad-ping" => "autocad",
        "models-mount" or "models-unmount" or "models-open" or "vpn-check" => "folders",
        _ => null
    };

    private string SelectedFolderResource()
    {
        var selected = ReadSafeField("share-resource");
        if (!string.IsNullOrWhiteSpace(selected)) return selected;
        var folders = _runtimeServices.CommonFolders.Folders;
        if (folders.Count == 1) return folders[0].ResourceId;
        throw new InvalidOperationException(folders.Count == 0 ? "Папки ещё не назначены администратором." : "Выберите папку проекта.");
    }

    private static void RequireAccepted(PlatformOperationResult result)
    {
        if (!result.Accepted) throw new InvalidOperationException(result.Code);
    }

    private void StartConverter(string command, CancellationToken cancellationToken)
    {
        string converter;
        NativeFile? input;
        NativeFile[] ifcInputs;
        string output;
        lock (_gate)
        {
            converter = command is "ifc-optimize" or "ifc-analyze" ? "ifc" : _converter;
            input = converter == "ifc" ? _ifcFiles.FirstOrDefault() : _files.FirstOrDefault();
            ifcInputs = _ifcFiles.ToArray();
            _converterSubmissionMode = _mode;
            output = _pickedFolders.GetValueOrDefault(converter == "ifc" ? "ifc-output" : "output-root")
                     ?? _converterWorkspace.OutputDirectory;
        }

        if (input is null) throw new InvalidOperationException("Сначала выберите исходный файл в системном диалоге.");
        if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("Сначала выберите папку результата.");

        _converterWorkspace.OutputDirectory = output;
        _converterWorkspace.Product = _mode == "platform" ? ConnectorProductId.Platform : ConnectorProductId.Structura;
        if (converter == "ifc")
        {
            _converterWorkspace.SetIfcInputPaths(ifcInputs.Select(file => file.Path));
            _converterWorkspace.IfcProfile = ReadSafeField("ifc-profile") is "compact" ? "compact" : "exact";
            StartAsyncCommand(command == "ifc-analyze" ? _converterWorkspace.AnalyzeIfcCommand : _converterWorkspace.SubmitIfcCommand, command, cancellationToken);
        }
        else
        {
            _converterWorkspace.FbxInputPath = input.Path;
            StartAsyncCommand(_converterWorkspace.SubmitFbxCommand, command, cancellationToken);
        }
    }

    private void PickInput(string target)
    {
        switch (target)
        {
            case "zip-input":
            {
                string? path = ChooseFile(target, "ZIP-архив модели|*.zip", ".zip");
                if (path is not null) lock (_gate) { _files.Clear(); _files.Add(NativeFile.FromPath(path)); }
                break;
            }
            case "folder-input":
            {
                string? path = ChooseFolder(target, "Папка с FBX и текстурами");
                if (path is not null) lock (_gate) { _files.Clear(); _files.Add(NativeFile.FromPath(path)); }
                break;
            }
            case "ifc-input":
            {
                string[]? paths = ChooseFiles(target, "IFC модели|*.ifc", ".ifc");
                if (paths is not null) lock (_gate)
                    foreach (string path in paths)
                        if (!_ifcFiles.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)))
                            _ifcFiles.Add(NativeFile.FromPath(path));
                break;
            }
            default:
                throw Unavailable("Неизвестный тип системного выбора файла.");
        }
    }

    private void PickFolder(string target)
    {
        if (!IsAllowedFolderTarget(target)) throw Unavailable("Эта папка не входит в native-контракт.");
        string? path = ChooseFolder(target, "Выберите папку");
        if (path is null) return;
        lock (_gate)
        {
            _pickedFolders[target] = path;
            _fields[target] = new DirectoryInfo(path).Name;
            if (target is "ifc-output" or "output-root") _converterWorkspace.OutputDirectory = path;
        }
    }

    private Task PersistSelectedFoldersAsync() => _connectorHost.SaveDesktopPreferencesAsync(CreatePreferences(null, null));

    private DesktopPreferences CreatePreferences(bool? autoStart, int? heartbeatSeconds)
    {
        lock (_gate) return new DesktopPreferences(
            _pickedFolders.GetValueOrDefault("xs-path"), _pickedFolders.GetValueOrDefault("ext-path"),
            _pickedFolders.GetValueOrDefault("lib-path"), _pickedFolders.GetValueOrDefault("sharing-bin"),
            _pickedFolders.GetValueOrDefault("ifc-bin"), _pickedFolders.GetValueOrDefault("patch-folder"),
            autoStart, heartbeatSeconds,
            _pickedFolders.ContainsKey("ifc-output") || _pickedFolders.ContainsKey("output-root")
                ? _converterWorkspace.OutputDirectory : null,
            _pickedFolders.GetValueOrDefault("publish-firm-path"),
            _pickedFolders.GetValueOrDefault("publish-ext-path"),
            _pickedFolders.GetValueOrDefault("publish-lib-path"));
    }

    private static TeklaPublicationRequest CreateTeklaPublicationRequest(JsonElement payload) => new(
        ReadBooleanField(payload, "publish-firm", false),
        ReadBooleanField(payload, "publish-extensions", false),
        ReadBooleanField(payload, "publish-libraries", false),
        ReadField(payload, "publish-comment").Trim());

    private string[]? ChooseFiles(string target, string filter, string extension)
    {
        string[]? paths;
        if (_pickFiles is not null) paths = _pickFiles(target);
        else if (_pickPath is not null) paths = _pickPath(target) is { } path ? [path] : null;
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter, Multiselect = true };
            paths = dialog.ShowDialog(_connectorHost.OwnerWindow) == true ? dialog.FileNames : null;
        }
        if (paths is null) return null;
        if (paths.Length > 128 || paths.Any(path => !Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Выберите не более 128 IFC-файлов.");
        return paths;
    }

    private string? ChooseFile(string target, string filter, string extension)
    {
        string? path;
        if (_pickPath is not null) path = _pickPath(target);
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter, Multiselect = false };
            path = dialog.ShowDialog(_connectorHost.OwnerWindow) == true ? dialog.FileName : null;
        }
        if (path is null) return null;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выбранный файл не соответствует ожидаемому типу.");
        return path;
    }

    private string? ChooseFolder(string target, string title)
    {
        string? path;
        if (_pickPath is not null) path = _pickPath(target);
        else
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
            path = dialog.ShowDialog(_connectorHost.OwnerWindow) == true ? dialog.FolderName : null;
        }
        if (path is null) return null;
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new InvalidOperationException("Выбранная папка недоступна.");
        return path;
    }

    private void ApplyPatchPickerPaths()
    {
        PatchingViewModel vm = Require(_patching, "Патчинг IFC");
        lock (_gate)
        {
            if (_pickedFolders.TryGetValue("ifc-bin", out string? bin)) vm.TeklaBin = bin;
            if (_pickedFolders.TryGetValue("patch-folder", out string? patch)) vm.StagingDir = patch;
        }
    }

    private void ApplySharingPickerPath()
    {
        ModelSharingViewModel vm = Require(_modelSharing, "Model Sharing");
        lock (_gate)
        {
            if (_pickedFolders.TryGetValue("sharing-bin", out string? bin)) vm.TeklaBin = bin;
        }
    }

    private void StartAsyncCommand(ICommand command, string name, CancellationToken cancellationToken)
    {
        if (command is not AsyncRelayCommand asyncCommand)
            throw new InvalidOperationException("Команда не поддерживает наблюдаемое async-выполнение.");
        if (!asyncCommand.CanExecute(null)) throw new InvalidOperationException("Действие сейчас недоступно.");
        StartOperation(() => asyncCommand.ExecuteAsync(), name, cancellationToken);
    }

    private void StartOperation(Func<Task> operation, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lifetime.IsCancellationRequested) throw new OperationCanceledException(_lifetime.Token);
        lock (_gate) _notice = new SafeNotice(name, "Действие запущено.");
        Task task = ObserveAsync(operation, name);
        lock (_gate) _pending.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (_gate) _pending.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ObserveAsync(Func<Task> operation, string name)
    {
        try
        {
            await operation();
            var outcome = LastOutcome(name);
            lock (_gate) _notice = new SafeNotice(name, OutcomeMessage(outcome, name));
        }
        catch (OperationCanceledException)
        {
            lock (_gate) _notice = new SafeNotice(name, "Действие отменено.");
        }
        catch (Exception ex)
        {
            lock (_gate) _notice = new SafeNotice(name, "Действие не выполнено. Подробности доступны в журнале.");
            _connectorHost.Log($"Graphite native {name} failed ({ex.GetType().Name}).");
        }
        finally
        {
            if (name is "start" or "ifc-optimize" or "ifc-analyze") lock (_gate) _converterSubmissionMode = null;
            PublishSnapshot();
        }
    }

    private OperationOutcome LastOutcome(string command) => command switch
    {
        "connect-common" => _commonAccess.IsReady ? OperationOutcome.Succeeded : OperationOutcome.None,
        "ifc-apply" or "ifc-rollback" => _patching?.LastOperationOutcome ?? OperationOutcome.Rejected,
        "sharing-config" => _modelSharing?.LastOperationOutcome ?? OperationOutcome.Rejected,
        "vpn-enable" or "vpn-disable" => _vpn?.LastOperationOutcome ?? OperationOutcome.Rejected,
        "connect" or "reconnect" => _connectorHost.IsConnected ? OperationOutcome.Succeeded : OperationOutcome.Failed,
        "connect-platform" => _platformConnection.Connection.IsConnected ? OperationOutcome.Succeeded : OperationOutcome.None,
        "start" or "ifc-optimize" or "ifc-analyze" => _converterWorkspace.SelectedJob?.State switch
        {
            "Готово" => OperationOutcome.Succeeded,
            "Отменено" => OperationOutcome.Cancelled,
            "Ошибка" or "Превышено время" or "Прервано" => OperationOutcome.Failed,
            _ => OperationOutcome.None
        },
        _ => OperationOutcome.None
    };

    private static string OutcomeMessage(OperationOutcome outcome, string command) => outcome switch
    {
        OperationOutcome.Succeeded => "Готово.",
        OperationOutcome.Cancelled => "Действие отменено.",
        OperationOutcome.Rejected when command is "ifc-apply" or "ifc-rollback" => "Закройте Tekla Structures и повторите действие.",
        OperationOutcome.Rejected => "Действие недоступно. Проверьте подключение и состояние приложения.",
        OperationOutcome.Failed => "Действие не выполнено. Подробности доступны в журнале.",
        _ => "Состояние обновлено."
    };

    private static void Execute(ICommand command)
    {
        if (!command.CanExecute(null)) throw new InvalidOperationException("Действие сейчас недоступно.");
        command.Execute(null);
    }

    private void ApplyRequestContext(JsonElement payload)
    {
        lock (_gate)
        {
            _mode = Allowed(ReadString(payload, "mode"), Modes, _mode);
            _page = Allowed(ReadString(payload, "page"), Pages, _page);
            _converter = Allowed(ReadString(payload, "converter"), Converters, _converter);
            _tekla = Allowed(ReadString(payload, "tekla"), TeklaPages, _tekla);
            if (payload.TryGetProperty("fields", out JsonElement fields) && fields.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty field in fields.EnumerateObject())
                {
                    if (!IsSafeFieldName(field.Name) || IsSensitive(field.Name) || IsAllowedFolderTarget(field.Name)) continue;
                    object? value = SafePrimitive(field.Value);
                    if (value is not null) _fields[field.Name] = value;
                }
            }
        }
    }

    private void RemoveIfc(int index)
    {
        lock (_gate)
        {
            if (index >= 0 && index < _ifcFiles.Count && !_converterWorkspace.IsSubmitting) _ifcFiles.RemoveAt(index);
        }
    }

    private void ClearFinishedIfc()
    {
        if (_converterWorkspace.IsSubmitting) return;
        lock (_gate) _ifcFiles.RemoveAll(file => CurrentFileState(file) is "Готово" or "Ошибка" or "Отменено");
    }

    private void ClearHistory()
    {
        if (_converterWorkspace.IsSubmitting) throw new InvalidOperationException("Дождитесь завершения задания.");
        _converterWorkspace.Jobs.Clear();
        _converterWorkspace.SelectedJob = null;
    }

    private void SelectRequestedJob(JsonElement payload)
    {
        string id = ReadString(payload, "target");
        if (string.IsNullOrWhiteSpace(id)) return;
        ConverterWorkspaceJob? job = _converterWorkspace.Jobs.FirstOrDefault(candidate => candidate.RequestId == id);
        if (job is not null) _converterWorkspace.SelectedJob = job;
    }

    private object JobSnapshot(ConverterWorkspaceJob job)
    {
        string mode = job.Product == ConnectorProductId.Platform ? "platform" : "structura";
        return new
        {
            id = job.RequestId,
            mode,
            kind = job.ExecutorId is ConverterWorkspaceViewModel.IfcOptimizeExecutor or ConverterWorkspaceViewModel.IfcAnalyzeExecutor ? "ifc" : "fbx",
            operation = job.Operation == ConverterJobOperation.Analyze ? "analyze" : "optimize",
            hasResult = !string.IsNullOrWhiteSpace(job.OutputPath),
            hasReport = !string.IsNullOrWhiteSpace(job.ReportPath),
            hasValidation = job.Operation == ConverterJobOperation.Optimize && !string.IsNullOrWhiteSpace(job.ReportPath),
            status = NormalizeJobState(job.State),
            progress = Math.Clamp(job.Progress ?? 0, 0, 100),
            output = SafeResultName(job.OutputPath)
        };
    }

    private string CurrentFileState(NativeFile file)
    {
        ConverterWorkspaceJob? job = _converterWorkspace.Jobs.FirstOrDefault(candidate =>
            string.Equals(candidate.InputPath, file.Path, StringComparison.OrdinalIgnoreCase));
        return job is null ? file.Status : job.State;
    }

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems is not null)
            foreach (ConverterWorkspaceJob job in args.OldItems)
            {
                Unsubscribe(job);
            }
        if (args.NewItems is not null)
            foreach (ConverterWorkspaceJob job in args.NewItems)
            {
                Subscribe(job);
            }
        PublishSnapshot();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args) => PublishSnapshot();
    private void OnPlatformSnapshotChanged(ConnectorRuntimeSnapshot snapshot) => PublishSnapshot();
    private void Subscribe(INotifyPropertyChanged? source) { if (source is not null) source.PropertyChanged += OnPropertyChanged; }
    private void Unsubscribe(INotifyPropertyChanged? source) { if (source is not null) source.PropertyChanged -= OnPropertyChanged; }

    private void PublishSnapshot()
    {
        if (_disposed) return;
        if (!_connectorHost.OwnerWindow.Dispatcher.CheckAccess())
        {
            _connectorHost.OwnerWindow.Dispatcher.BeginInvoke(new Action(PublishSnapshot));
            return;
        }
        try { SnapshotChanged?.Invoke(CreateSnapshot()); }
        catch (Exception) { _connectorHost.Log("Graphite native snapshot publish failed."); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_converterWorkspace.CancelCommand.CanExecute(null)) _converterWorkspace.CancelCommand.Execute(null);
        if (_platformTools.CancelCommand.CanExecute(null)) _platformTools.CancelCommand.Execute(null);

        _converterWorkspace.Jobs.CollectionChanged -= OnJobsChanged;
        Unsubscribe(_platformTools);
        Unsubscribe(_converterWorkspace);
        Unsubscribe(_patching);
        Unsubscribe(_modelSharing);
        Unsubscribe(_vpn);
        Unsubscribe(_structura);
        _runtimeServices.Platform.SnapshotChanged -= OnPlatformSnapshotChanged;
        foreach (ConverterWorkspaceJob job in _converterWorkspace.Jobs) Unsubscribe(job);

        Task[] pending;
        lock (_gate) pending = _pending.ToArray();
        try { await Task.WhenAll(pending); }
        catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    private static T? ViewModel<T>(FeatureDomain domain, string title) where T : class =>
        domain.Module(title)?.View.DataContext as T;

    private static T Require<T>(T? value, string name) where T : class =>
        value ?? throw Unavailable(name + " недоступен в текущей композиции приложения.");

    private static NotSupportedException Unavailable(string message) => new(message);
    private static string Allowed(string value, HashSet<string> allowed, string fallback) => allowed.Contains(value) ? value : fallback;
    private static string NormalizeJobState(string state) => state switch
    {
        "Готово" => "Готово", "Отменено" => "Отменено", "Ошибка" => "Ошибка", "Превышено время" => "Ошибка",
        "В очереди" or "Принято исполнителем" or "Выполняется" => "Выполняется", _ => "—"
    };
    private static string SafeResultName(string path) => string.IsNullOrWhiteSpace(path) ? "" : Path.GetFileName(path);
    private string SafeModelSharingStatus() => _modelSharing switch
    {
        null => "Недоступно",
        { IsBusy: true } => "Настройка выполняется",
        { SetupEnabled: true } => "Доступно для настройки",
        _ => "Недоступно до подключения и проверки Tekla"
    };
    private string SafePatchStatus() => _patching switch
    {
        null => "Недоступно",
        { IsBusy: true } => "Операция выполняется",
        { IsApplied: true } => "Патч установлен",
        _ => "Патч не установлен или ещё не проверен"
    };
    private string SafeVpnStatus() => _vpn switch
    {
        null => "Недоступно",
        { IsBusy: true } => "Операция выполняется",
        { CanDisable: true } => "VPN включён или установлен",
        { CanEnable: true } => "VPN доступен для подключения",
        _ => "VPN недоступен"
    };
    private static object SafeFile(NativeFile file) => SafeFile(file, file.Status);
    private static object SafeFile(NativeFile file, string status) => new { file.Name, file.Size, status };
    private static bool IsSensitive(string name) =>
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cipher", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase);
    private static bool IsSafeFieldName(string name) =>
        name.Length is > 0 and <= 80 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static string ResolveApplicationVersion()
    {
        string? informationalVersion = typeof(GraphiteDesktopController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2)[0]
            .Trim();
        return !string.IsNullOrWhiteSpace(informationalVersion) && informationalVersion.Length <= 64 &&
               informationalVersion.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
            ? informationalVersion
            : "—";
    }
    private static object? SafePrimitive(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when value.GetString() is { Length: <= 2048 } text => text,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt32(out int number) => number,
        _ => null
    };
    private static string ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    private static string ReadField(JsonElement payload, string name) =>
        payload.TryGetProperty("fields", out JsonElement fields) && fields.ValueKind == JsonValueKind.Object &&
        fields.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    private static bool ReadBooleanField(JsonElement payload, string name, bool fallback) =>
        payload.TryGetProperty("fields", out JsonElement fields) && fields.ValueKind == JsonValueKind.Object &&
        fields.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
    private static int ReadIntField(JsonElement payload, string name, int fallback)
    {
        if (!payload.TryGetProperty("fields", out JsonElement fields) || fields.ValueKind != JsonValueKind.Object ||
            !fields.TryGetProperty(name, out JsonElement value)) return fallback;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed)) return parsed;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out parsed)) return parsed;
        return fallback;
    }
    private string ReadSafeField(string name)
    {
        lock (_gate) return _fields.TryGetValue(name, out object? value) ? value?.ToString() ?? string.Empty : string.Empty;
    }
    private static int ReadIndex(JsonElement payload) =>
        payload.TryGetProperty("index", out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        int.TryParse(value.GetString(), out int index) ? index : -1;
    private static bool IsAllowedFolderTarget(string target) => target is
        "xs-path" or "ext-path" or "lib-path" or "sharing-bin" or "ifc-bin" or "patch-folder" or
        "export-model-folder" or "export-output" or "output-root" or "ifc-output" or
        "publish-firm-path" or "publish-ext-path" or "publish-lib-path";

    private sealed record NativeFile(string Path, string Name, long Size, string Status)
    {
        public static NativeFile FromPath(string path)
        {
            long size = File.Exists(path) ? new FileInfo(path).Length : 0;
            string name = File.Exists(path) ? System.IO.Path.GetFileName(path) : new DirectoryInfo(path).Name;
            return new NativeFile(path, name, size, "Выбран");
        }
    }

    private sealed record SafeNotice(string Command, string Message);
}
