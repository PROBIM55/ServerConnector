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
using Connector.Network;
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
    private static readonly string? ApplicationBuild = ResolveApplicationBuild();
    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "structura", "platform" };
    private static readonly HashSet<string> Converters = new(StringComparer.Ordinal) { "catalog", "fbx", "ifc", "history" };
    private static readonly HashSet<string> TeklaPages = new(StringComparer.Ordinal) { "standard", "sharing", "ifc", "exports" };
    private static readonly HashSet<string> Pages = new(StringComparer.Ordinal)
    {
        "overview", "services", "tekla", "attributes", "folders", "converters", "agr", "bridge",
        "autocad", "jobs", "settings", "admin"
    };

    private static readonly TimeSpan LegacyWatchInterval = TimeSpan.FromSeconds(30);
    // Fixed destinations only: the web document names an action, never a URL or a path.
    private const string PlatformUrl = "https://bimplatforma.ru/";
    private const string LicensesFileName = "THIRD_PARTY_NOTICES.txt";
    private const string LegacyStartedMessage = "Запущен прежний Structura Connector. Компьютер переведён на " +
        "Платформа Коннектор, и прежний коннектор может отключить папку BIM_Models. Закройте его через значок " +
        "в области уведомлений («Закрыть») или верните его кнопкой «Вернуть прежний коннектор» в настройках.";

    private readonly object _gate = new();
    private readonly IConnectorHost _connectorHost;
    private readonly ConnectorRuntimeServices _runtimeServices;
    private readonly PlatformToolsViewModel _platformTools;
    private readonly IPlatformConnectionApi _platformConnection;
    private readonly ConnectorBrowserSignIn _browserSignIn = new();
    private readonly CommonAccessRuntime _commonAccess;
    // Confirmations are dialogs of the page; the native action asks and waits for the explicit answer.
    private readonly PageConfirmationBroker _confirmations = new();
    private readonly Action<string>? _legacyStartedNotice;
    private readonly Dictionary<string, (long? Input, long? Output)> _converterSizes = new(StringComparer.Ordinal);
    // An operation may report its own outcome text (for example, the found version); it replaces the generic line.
    private readonly Dictionary<string, (string Message, string Kind)> _operationResults = new(StringComparer.Ordinal);
    private string? _webViewVersion;
    private bool _webViewVersionRead;
    private string? _ifcEngineVersion;
    private bool _ifcEngineVersionRead;
    private volatile LegacyTransferStatus _legacyStatus = new(false, false, null);
    private int _legacyBusy;
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
    private int _browserSignInPending;
    private string? _converterSubmissionMode;
    private IReadOnlyList<PlatformAutoCadSession> _cadSessions = Array.Empty<PlatformAutoCadSession>();
    private long _noticeSeq;
    private volatile bool _windowMaximized;
    private volatile bool _nativeCaption;
    private object? _agrRuntime;
    private bool _agrRuntimeRead;
    private string _uiTheme;

    public GraphiteDesktopController(
        IConnectorHost connectorHost,
        ConnectorRuntimeServices runtimeServices,
        ShellViewModel shell,
        PlatformToolsViewModel platformTools,
        ConverterWorkspaceViewModel converterWorkspace,
        Func<string, string?>? pickPath = null,
        Func<string, string[]?>? pickFiles = null,
        IPlatformConnectionApi? platformConnection = null,
        Action<string>? legacyStartedNotice = null,
        string? journalPath = null)
    {
        _connectorHost = connectorHost ?? throw new ArgumentNullException(nameof(connectorHost));
        _runtimeServices = runtimeServices ?? throw new ArgumentNullException(nameof(runtimeServices));
        ArgumentNullException.ThrowIfNull(shell);
        _platformTools = platformTools ?? throw new ArgumentNullException(nameof(platformTools));
        _platformConnection = platformConnection ?? runtimeServices.Platform;
        _commonAccess = runtimeServices.CommonAccess;
        _runtimeServices.CommonOperations.Changed += OnCommonOperationChanged;
        _runtimeServices.CommonRecoveryChanged += OnCommonRecoveryChanged;
        _legacyStartedNotice = legacyStartedNotice;
        Journal = new ConnectorJobJournal(journalPath ?? ConnectorJobJournal.DefaultPath, log: _connectorHost.Log);
        Journal.Changed += PublishSnapshot;
        _confirmations.Changed += PublishSnapshot;
        _runtimeServices.Host.JobPicked += OnRuntimeJobPicked;
        _runtimeServices.Host.JobStatusChanged += OnRuntimeJobStatus;
        _runtimeServices.Platform.SnapshotChanged += OnPlatformSnapshotChanged;
        _converterWorkspace = converterWorkspace ?? throw new ArgumentNullException(nameof(converterWorkspace));
        if (Path.IsPathFullyQualified(_connectorHost.Settings.ConverterOutputDirectory))
            _converterWorkspace.OutputDirectory = _connectorHost.Settings.ConverterOutputDirectory;
        _fields["output-root"] = _converterWorkspace.OutputDirectory;
        _fields["ifc-output"] = _converterWorkspace.OutputDirectory;
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
        _legacyStatus = ReadLegacyStatus();
        TrackOperation(WatchLegacyConnectorAsync(_lifetime.Token));
        _uiTheme = GraphiteCommandRules.NormalizeTheme(_connectorHost.Settings.UiTheme) ?? "dark";
        _connectorHost.OwnerWindow.StateChanged += OnOwnerWindowStateChanged;
        _windowMaximized = _connectorHost.OwnerWindow.WindowState == System.Windows.WindowState.Maximized;
    }

    public event Action<object>? SnapshotChanged;

    /// <summary>Recent jobs of this connector: Platform jobs it executed and local operations («Задания»).</summary>
    public ConnectorJobJournal Journal { get; }

    /// <summary>Asks the user in the page (one dialog) and waits for the answer; false when the window closes.</summary>
    public Task<bool> ConfirmAsync(PageConfirmation request) => _confirmations.RequestAsync(request, _lifetime.Token);

    /// <summary>Model Sharing progress reported into its journal entry.</summary>
    public static ModelSharingProgressHandle ModelSharingProgress(ILocalJobProgress progress) => new()
    {
        StepAction = (step, details, current, total, _) => progress.Step(step, details, total > 0 ? (current - 1) * 100 / total : null),
        SucceededAction = details => progress.Succeed(details),
        FailedAction = details => progress.Fail(details),
        DismissAction = progress.Cancel
    };

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
        // The full local path is shown (the page never sends a path back: folder targets are ignored in
        // request fields). Restoration performs no I/O.
        _savedFields[name] = (_pickedFolders.GetValueOrDefault(name) ?? path ?? string.Empty).Trim();
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

        object[] jobs = GraphiteSnapshotFacts.Jobs(Journal.Entries, _converterWorkspace.Jobs.Select(ConverterFacts).ToArray());
        Dictionary<string, string> picked;
        lock (_gate) picked = new Dictionary<string, string>(_pickedFolders, StringComparer.Ordinal);
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

        var connectionProgress = _runtimeServices.CommonOperations.Progress;
        var recoveryProgress = _runtimeServices.CommonRecoveryProgress;
        var commonBusy = connectionProgress.Operation is not null || recoveryProgress.Length != 0;
        var progressMessage = connectionProgress.Operation is not null ? connectionProgress.Message : recoveryProgress;
        var commonStatus = commonBusy ? progressMessage : _commonAccess.DisplayStatus;
        if (notice?.ConnectionGeneration is { } noticeGeneration && noticeGeneration != connectionProgress.Generation)
            notice = null;
        if (_commonAccess.IsSelected && commonBusy)
        {
            moduleFields["vpn-status"] = commonStatus;
            moduleFields["agent-status"] = commonStatus;
        }
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
            ["commonAccessReady"] = _commonAccess.IsReady,
            ["commonAccessNeedsSignIn"] = _commonAccess.Current?.Status is
                CommonConnectorConnectionStatus.EnrollmentRequired or CommonConnectorConnectionStatus.Revoked,
            ["signInPending"] = Volatile.Read(ref _browserSignInPending) != 0,
            ["connectPending"] = commonBusy,
            ["signOutPending"] = connectionProgress.Operation == CommonConnectionOperation.SignOut,
            ["connectionProgress"] = progressMessage,
            // One interface for both products: the folder list follows the grant of either product,
            // not the product of the last command.
            ["assignedFolders"] = _commonAccess.IsSelected &&
                (_commonAccess.HasPermission("structura", "folders", ConnectorPermission.Read) ||
                 _commonAccess.HasPermission("platform", "folders", ConnectorPermission.Read))
                ? _runtimeServices.CommonFolders.Folders : Array.Empty<CommonSmbFolder>(),
            ["folderStatus"] = _runtimeServices.CommonFolderStatus,
            ["legacyTransfer"] = LegacyTransferSnapshot(),
            ["jobs"] = jobs,
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
            ["status"] = _commonAccess.IsSelected ? commonStatus : _connectorHost.IsConnected ? "Подключено" : "Не подключено",
            ["hasSavedCredential"] = _commonAccess.IsSelected ? _commonAccess.HasStoredCredential : !string.IsNullOrWhiteSpace(_connectorHost.Settings.TokenCipherBase64),
            ["connections"] = new
            {
                structura = new { status = _commonAccess.IsSelected ? commonStatus : _connectorHost.IsConnected ? "Подключено" : "Не подключено",
                    hasSavedCredential = _commonAccess.IsSelected ? _commonAccess.HasStoredCredential : !string.IsNullOrWhiteSpace(_connectorHost.Settings.TokenCipherBase64) },
                platform = new { status = _commonAccess.IsSelected ? commonStatus : PlatformStatus(), hasSavedCredential = _commonAccess.IsSelected ? _commonAccess.HasStoredCredential : _platformConnection.Connection.HasStoredCredential }
            },
            ["cadSessions"] = _cadSessions,
            ["notice"] = notice,
            ["account"] = _commonAccess.IsSelected
                ? ConnectorAccountFacts.Build(Environment.MachineName, _commonAccess.LastOverlay, _commonAccess.GatewayUri)
                : null,
            ["teklaStandard"] = GraphiteSnapshotFacts.TeklaStandard(_connectorHost.Settings, picked),
            ["tekla"] = TeklaSnapshot(),
            ["update"] = GraphiteSnapshotFacts.Update(_connectorHost.UpdateInfo, _connectorHost.HasPendingUpdate),
            ["about"] = AboutSnapshot(),
            ["agrInstall"] = _platformTools.AgrInstallProgress is { } install
                ? new { phase = install.Phase, completedBytes = install.CompletedBytes, totalBytes = install.TotalBytes }
                : null,
            ["confirmation"] = GraphiteSnapshotFacts.Confirmation(_confirmations.Current),
            ["uiTheme"] = _uiTheme,
            ["windowMaximized"] = _windowMaximized,
            ["nativeCaption"] = _nativeCaption,
            ["agrRuntime"] = AgrRuntimeFacts()
        };
        return snapshot;
    }

    public Task<object?> HandleAsync(GraphiteRpcRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        // Messages of GraphiteUserException are fixed user texts of this boundary; the document shows them as is.
        return GraphiteCommandRules.RefuseUserErrors(() => HandleCore(request, cancellationToken), message =>
        {
            _connectorHost.Log($"Graphite native {request.Command} refused: {message}");
            return Availability(false, message);
        });
    }

    private Task<object?> HandleCore(GraphiteRpcRequest request, CancellationToken cancellationToken)
    {
        ApplyRequestContext(request.Payload);

        if (_commonAccess.IsSelected)
        {
            var module = CommandModule(request.Command);
            var permission = request.Command is "models-mount" or "models-unmount" or "models-open" or "vpn-check" or
                "firm-sync-all" or "xs-sync" or "ext-sync" or "lib-sync" or "legacy-transfer" or
                "xs-open" or "ext-open" or "lib-open"
                ? ConnectorPermission.Read : ConnectorPermission.Execute;
            if (module is not null && !_commonAccess.HasPermission(_mode, module, permission))
                return Availability(false, "Для этого действия нет доступа. Обратитесь к администратору.");
        }

        switch (request.Command)
        {
            case "signout":
                StartCommonSignOut(cancellationToken);
                break;
            case "connect":
                if (_commonAccess.ShouldUse()) StartCommonConnect(null, false, cancellationToken);
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
                StartOperation(CheckUpdatesAsync, request.Command, cancellationToken);
                break;
            case "confirm-answer":
                // Explicit answer of the page dialog. Only the id of the confirmation the native action is waiting
                // for is accepted, once; the message itself came through the trusted-document RPC channel.
                if (!_confirmations.Answer(ReadString(request.Payload, "target"), ReadBooleanField(request.Payload, "accept", false)))
                    return Availability(false, "Это подтверждение уже не действует.");
                break;
            case "drop-input":
                DropInput(ReadString(request.Payload, "target"), request.DroppedPaths ?? Array.Empty<string>());
                break;
            case "update-download":
                StartSupportAction(DesktopSupportAction.DownloadPendingUpdate, request.Command, cancellationToken);
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
            case "update-apply":
                // The page asks first (its own dialog), like the update toast; one confirmation only.
                StartOperation(() => _connectorHost.InstallPendingUpdateAsync(confirmBeforeRun: false), request.Command, cancellationToken);
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
                Journal.RemoveFinished();
                break;
            case "settings-store":
                StartSavePreferences(request.Payload, reconnect: false, request.Command, cancellationToken);
                break;
            case "ifc-detect":
                StartAsyncCommand(Require(_patching, "Патчинг IFC").DetectCommand, request.Command, cancellationToken);
                break;
            case "ifc-apply":
            case "ifc-rollback":
            {
                ApplyPatchPickerPaths();
                var patching = Require(_patching, "Патчинг IFC");
                var apply = request.Command == "ifc-apply";
                StartJournaledCommand(apply ? patching.ApplyCommand : patching.RollbackCommand, request.Command, "patch",
                    apply ? "Установка патча IFC-экспорта" : "Возврат исходных файлов IFC-экспорта",
                    apply ? "Замена файлов IFC-экспорта" : "Возврат исходных файлов",
                    () => patching.LastOperationOutcome, () => patching.ResultMessage, cancellationToken);
                break;
            }
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
                    var folderName = _runtimeServices.CommonFolders.Folders.FirstOrDefault(folder => folder.ResourceId == resource)?.DisplayName ?? "Папка";
                    StartOperation(() => _runtimeServices.CommonFolders.UnmountAsync(resource, drive,
                        () => ConfirmAsync(new PageConfirmation(
                            $"Отключить диск {drive}?",
                            $"Папка «{folderName}» перестанет быть видна как диск {drive}. Файлы на сервере не меняются.",
                            [$"Закройте файлы, открытые с диска {drive}.", "Открыть папку можно снова в любой момент."],
                            null, "Отключить", Danger: true)), cancellationToken), request.Command, cancellationToken);
                }
                else StartOperation(() => _connectorHost.UnmountVpnShareAsync(ReadSafeField("drive")), request.Command, cancellationToken);
                break;
            case "vpn-check":
                if (_commonAccess.IsSelected)
                    StartOperation(async () =>
                    {
                        using var response = await _commonAccess.SendAsync(System.Net.Http.HttpMethod.Head, "jobs/health", null,
                            new Dictionary<string, string>(), cancellationToken);
                        if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
                            throw UserError("Не удалось подтвердить защищённое подключение.");
                    }, request.Command, cancellationToken);
                else Require(_vpn, "VPN").RefreshStatus();
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
            case "legacy-transfer":
            case "legacy-restore":
                if (!StartLegacyAction(request.Command, request.Command == "legacy-transfer"
                        ? TransferLegacyConnectorAsync : RestoreLegacyConnectorAsync, cancellationToken))
                    return Availability(false, "Действие с прежним коннектором уже выполняется.");
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
            case "window-minimize":
            case "window-maximize":
            case "window-close":
                // «window-close» takes the path of the window cross: the Closing handler keeps the connector in the tray.
                var windowCommand = request.Command;
                OnOwnerWindow(window =>
                {
                    if (GraphiteCommandRules.NextWindowState(windowCommand, window.WindowState) is { } state) window.WindowState = state;
                    else window.Close();
                });
                break;
            case "ui-theme":
                SaveTheme(ReadField(request.Payload, "ui-theme"));
                break;
            case "platform-open":
                OpenWithShell(PlatformUrl, "Не удалось открыть Платформу в браузере.");
                break;
            case "xs-open":
            case "ext-open":
            case "lib-open":
                OpenTeklaFolder(request.Command);
                break;
            case "licenses-open":
                var licenses = Path.Combine(AppContext.BaseDirectory, LicensesFileName);
                if (!File.Exists(licenses)) throw UserError("Файл лицензий не найден. Переустановите Платформа Коннектор.");
                OpenWithShell(licenses, "Не удалось открыть файл лицензий.");
                break;
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
            throw UserError("Нужен действующий токен устройства.");

        StartOperation(async () =>
        {
            await _connectorHost.ConnectByTokenAsync(token, showSuccessDialog: true);
        }, "connect", cancellationToken);
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
        if (string.IsNullOrWhiteSpace(token)) throw UserError("Сохранённый токен отсутствует.");
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
            RequireAccepted(reconnect
                ? await _platformConnection.ReconnectAsync(cancellationToken)
                : await _platformConnection.ConnectAsync(token, cancellationToken));
        }, "connect-platform", cancellationToken);
    }

    private void StartCommonConnect(string? token, bool reconnect, CancellationToken cancellationToken)
    {
        _runtimeServices.CancelCommonConnectionIntent();
        StartCommonOperation(reconnect ? CommonConnectionOperation.Restore : CommonConnectionOperation.SignIn,
            async (attempt, ct) =>
        {
            _commonAccess.Select();
            if (!reconnect)
            {
                Volatile.Write(ref _browserSignInPending, 1);
                PublishSnapshot();
                try
                {
                    token = await _browserSignIn.SignInAsync(attempt.Report, ct);
                }
                finally
                {
                    Volatile.Write(ref _browserSignInPending, 0);
                    PublishSnapshot();
                }
            }
            ct.ThrowIfCancellationRequested();
            if (_connectorHost.IsConnected) await _connectorHost.DisconnectAsync();
            var nativeStarted = System.Diagnostics.Stopwatch.StartNew();
            void ReportNativePhase(string text)
            {
                attempt.Report(text);
                var phase = text switch
                {
                    "Завершаем текущую работу…" => "drain",
                    "Проверяем сетевые компоненты…" => "network-components",
                    "Восстанавливаем вход и защищённое соединение…" => "restore",
                    "Вход подтверждён. Настраиваем защищённое соединение…" => "enroll",
                    "Ожидаем подтверждения доступа от сервера…" => "providers",
                    "Проверяем защищённое соединение…" => "final-route",
                    "Запускаем фоновую работу…" => "start-worker",
                    "Подключаем общие папки…" => "folders",
                    _ => null
                };
                if (phase is not null)
                    _connectorHost.Log($"Common connection native phase {phase} at {nativeStarted.Elapsed.TotalSeconds:F1}s.");
            }
            await _runtimeServices.ConnectCommonAsync(token, reconnect, ct, ReportNativePhase);
            _connectorHost.Log($"Common connection native completed in {nativeStarted.Elapsed.TotalSeconds:F1}s.");
        }, "connect-common", cancellationToken);
    }

    private void StartCommonSignOut(CancellationToken cancellationToken)
    {
        _runtimeServices.CancelCommonConnectionIntent();
        StartCommonOperation(CommonConnectionOperation.SignOut, async (attempt, ct) =>
        {
            if (_commonAccess.HasStoredCredential)
                await _runtimeServices.SignOutCommonAsync(ct);
            // Cancelling a browser request before enrollment has no device to revoke.
        }, "signout", cancellationToken);
    }

    private void StartCommonOperation(CommonConnectionOperation operation,
        Func<CommonConnectionAttempt, CancellationToken, Task> execute, string name, CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        CommonConnectionAttempt attempt;
        try { attempt = _runtimeServices.CommonOperations.Start(operation, execute, linked.Token); }
        catch { linked.Dispose(); throw; }
        lock (_gate) _notice = null;
        PublishSnapshot();
        var task = ObserveCommonOperationAsync(attempt, name, linked);
        TrackOperation(task);
    }

    private async Task ObserveCommonOperationAsync(CommonConnectionAttempt attempt, string name, IDisposable lifetime)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        string message;
        string kind = "error";
        try
        {
            await attempt.Completion;
            kind = attempt.Operation == CommonConnectionOperation.SignOut || _commonAccess.IsReady ? "ok" : "warn";
            message = attempt.Operation == CommonConnectionOperation.SignOut ?
                _runtimeServices.CommonFolderCleanupPending
                    ? "Вы вышли из учётной записи. Некоторые сетевые папки заняты — закройте открытые файлы."
                    : "Вы вышли из учётной записи." :
                _commonAccess.IsReady ? "Подключено." : _commonAccess.DisplayStatus;
        }
        catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested) { message = "Действие отменено."; kind = "info"; }
        catch (ConnectorSignInException ex) { message = ex.Message; }
        catch (Exception ex)
        {
            message = attempt.Operation == CommonConnectionOperation.SignOut ?
                "Выход не завершён. Проверьте соединение и повторите попытку." :
                _commonAccess.DisplayStatus == "Требуется вход" ? "Войдите через Платформу повторно." :
                "Не удалось завершить подключение. Повторите попытку.";
            // Codes are defined locally; never log an exception message/body,
            // which can carry server credentials or transport identifiers.
            var code = ex switch
            {
                CommonSmbAccessException smb when smb.Code is "smb_route_changed" or
                    "smb_route_cleanup_incomplete" or "smb_cleanup_pending" => smb.Code,
                Connector.Network.NetworkGateException => "network_gate",
                _ => "unspecified"
            };
            _connectorHost.Log($"Common connection {attempt.Operation} failed ({ex.GetType().Name}, {code}).");
        }
        finally { lifetime.Dispose(); }
        if (_runtimeServices.CommonOperations.IsLatest(attempt.Generation))
            lock (_gate) _notice = Notice(name, message, kind, attempt.Generation);
        _connectorHost.Log($"Common connection {attempt.Operation} finished in {started.Elapsed.TotalSeconds:F1}s.");
        PublishSnapshot();
    }

    private static string? CommandModule(string command) => command switch
    {
        "start" or "ifc-optimize" or "ifc-analyze" => "converters",
        "ifc-detect" or "ifc-apply" or "ifc-rollback" or "sharing-config" => "tekla",
        "agr-probe" or "agr-install" => "agr",
        "bridge-check" or "bridge-start" or "bridge-restart" => "tekla",
        "firm-sync-all" or "xs-sync" or "ext-sync" or "lib-sync" => "tekla",
        "xs-open" or "ext-open" or "lib-open" => "tekla",
        "cad-refresh" or "cad-ping" => "autocad",
        "models-mount" or "models-unmount" or "models-open" or "vpn-check" => "folders",
        // The transfer hands BIM_Models to this connector: the same Read right as opening a folder.
        // «legacy-restore» is deliberately ungated: it only returns this PC to the previous client
        // and must stay possible when the new connection or its grants are gone.
        "legacy-transfer" => "folders",
        _ => null
    };

    private string SelectedFolderResource()
    {
        var selected = ReadSafeField("share-resource");
        if (!string.IsNullOrWhiteSpace(selected)) return selected;
        var folders = _runtimeServices.CommonFolders.Folders;
        if (folders.Count == 1) return folders[0].ResourceId;
        throw UserError(folders.Count == 0 ? "Папки ещё не назначены администратором." : "Выберите папку проекта.");
    }

    private static void RequireAccepted(PlatformOperationResult result)
    {
        if (!result.Accepted) throw new InvalidOperationException(result.Code);
    }

    private bool StartLegacyAction(string name, Func<CancellationToken, Task<(string Message, string Kind)>> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lifetime.IsCancellationRequested) throw new OperationCanceledException(_lifetime.Token);
        if (Interlocked.CompareExchange(ref _legacyBusy, 1, 0) != 0) return false;
        lock (_gate) _notice = Notice(name, "Действие запущено.", "progress");
        // Native steps (window messages, registry, WNet) never run on the UI thread.
        TrackOperation(Task.Run(() => ObserveLegacyActionAsync(name, action, cancellationToken)));
        return true;
    }

    private async Task ObserveLegacyActionAsync(string name, Func<CancellationToken, Task<(string Message, string Kind)>> action,
        CancellationToken cancellationToken)
    {
        string message;
        string kind;
        try { (message, kind) = await action(cancellationToken); }
        catch (OperationCanceledException) { message = "Действие отменено."; kind = "info"; }
        catch (Exception ex)
        {
            message = "Действие не выполнено. Подробности доступны в журнале.";
            kind = "error";
            _connectorHost.Log($"Legacy connector {name} failed ({ex.GetType().Name}).");
        }
        finally
        {
            _legacyStatus = ReadLegacyStatus();
            Volatile.Write(ref _legacyBusy, 0);
        }
        lock (_gate) _notice = Notice(name, message, kind);
        PublishSnapshot();
    }

    private static string OutcomeKind(LegacyTransferOutcome outcome) => outcome switch
    {
        LegacyTransferOutcome.Completed => "ok",
        LegacyTransferOutcome.AlreadyDone => "info",
        _ => "error"
    };

    private async Task<(string Message, string Kind)> TransferLegacyConnectorAsync(CancellationToken cancellationToken)
    {
        var service = _runtimeServices.LegacyTransfer;
        var (readiness, grantKnown) = ReadLegacyReadiness();
        if (readiness.ConnectionReady && !grantKnown)
            return ("Список общих папок ещё не получен, поэтому доступ к папке BIM_Models не проверен. Откройте " +
                "раздел «Папки», дождитесь списка папок и повторите перевод.", "warn");
        var preflight = service.Preflight(readiness);
        if (preflight.Transferred) return ("Компьютер уже переведён на Платформа Коннектор.", "info");
        if (preflight.Blockers.Count > 0)
        {
            _connectorHost.Log($"Legacy connector transfer refused (ready={readiness.ConnectionReady}, " +
                $"granted={readiness.LegacyShareGranted}, incomplete={preflight.Incomplete}).");
            return ("Перевод недоступен. " + string.Join(" ", preflight.Blockers), "error");
        }
        var note = "Закройте файлы, открытые из BIM_Models." +
            (preflight.Warnings.Count == 0 ? string.Empty : " Внимание: " + string.Join(" ", preflight.Warnings));
        if (!await ConfirmAsync(new PageConfirmation(
                "Перевести компьютер на Платформа Коннектор?",
                "После перевода BIM_Models открывается здесь, а прежний коннектор не мешает.",
                [
                    "Прежний Structura Connector закроется и не будет запускаться с Windows.",
                    "Будут сняты подключения Windows к серверу BIM_Models без буквы диска, в том числе созданные Tekla, " +
                    "AutoCAD или проводником под учётной записью прежнего коннектора.",
                    "Его токен, настройки и VPN не изменяются — Tekla MultiUser работает как раньше.",
                    "Вернуть прежний коннектор можно одной кнопкой в «Настройках»."
                ],
                note, "Перевести", Danger: false, Cancel: "Не сейчас")))
            return ("Перевод отменён.", "info");

        var result = await service.TransferAsync(new LegacyTransferRequest(true, readiness, () => ConfirmAsync(new PageConfirmation(
            "Завершить прежний коннектор принудительно?",
            "Прежний Structura Connector не закрылся сам.",
            ["Незавершённые действия прежнего коннектора будут прерваны."],
            null, "Завершить", Danger: true))), cancellationToken);
        _connectorHost.Log($"Legacy connector transfer {result.Outcome} ({result.Code}).");
        if (result.Outcome == LegacyTransferOutcome.Completed) await RefreshLegacyFolderFlagsAsync();
        return (result.Message, OutcomeKind(result.Outcome));
    }

    private async Task<(string Message, string Kind)> RestoreLegacyConnectorAsync(CancellationToken cancellationToken)
    {
        var service = _runtimeServices.LegacyTransfer;
        if (!service.ReadStatus().CanRestore) return ("Компьютер не переведён: прежний коннектор работает как раньше.", "info");
        if (!await ConfirmAsync(new PageConfirmation(
                "Вернуть прежний Structura Connector?",
                "Папку BIM_Models снова будет обслуживать прежний коннектор.",
                [
                    "Диск Платформа Коннектор с папкой BIM_Models будет отключён.",
                    "Автозапуск прежнего коннектора будет восстановлен, а сам он запущен.",
                    "Перевести компьютер снова можно в любой момент."
                ],
                "Закройте файлы, открытые из BIM_Models.", "Вернуть", Danger: true)))
            return ("Возврат отменён.", "info");

        var result = await service.RestoreAsync(
            ct => _runtimeServices.CommonFolders.ReleaseOwnedLegacyShareAsync(ct), cancellationToken);
        _connectorHost.Log($"Legacy connector restore {result.Outcome} ({result.Code}).");
        if (result.Outcome == LegacyTransferOutcome.Completed) await RefreshLegacyFolderFlagsAsync();
        return (result.Message, OutcomeKind(result.Outcome));
    }

    /// <summary>
    /// Decides the grant from the folder list published by the latest server grant. No refresh here:
    /// a failed refresh clears the list (fail-closed) and must not be reported as a missing grant.
    /// </summary>
    private (LegacyTransferReadiness Readiness, bool GrantKnown) ReadLegacyReadiness()
    {
        var ready = _commonAccess.IsSelected && _commonAccess.IsReady;
        var folders = _runtimeServices.CommonFolders.Folders;
        var tekla = _commonAccess.HasPermission("structura", "tekla", ConnectorPermission.Read) ||
            _commonAccess.HasPermission("platform", "tekla", ConnectorPermission.Read);
        return (new LegacyTransferReadiness(ready, folders.Any(folder => folder.LegacyShare), tekla), folders.Count > 0);
    }

    /// <summary>Best effort: the folder list carries the neighbour-mode flag of its last evaluation.</summary>
    private async Task RefreshLegacyFolderFlagsAsync()
    {
        if (!_commonAccess.IsReady || !_runtimeServices.CommonFolders.IsAuthorizationAdmissionOpen) return;
        try { await _runtimeServices.CommonFolders.RefreshAvailableAsync(_lifetime.Token); }
        catch (Exception ex)
        {
            // The transfer or rollback itself is complete; the list refreshes on the next folder action.
            _connectorHost.Log($"Legacy connector folder refresh failed ({ex.GetType().Name}).");
        }
    }

    private LegacyTransferStatus ReadLegacyStatus()
    {
        try { return _runtimeServices.LegacyTransfer.ReadStatus(); }
        catch (Exception) { return _legacyStatus; }
    }

    private object LegacyTransferSnapshot()
    {
        var status = _legacyStatus;
        var busy = Volatile.Read(ref _legacyBusy) != 0;
        var text = busy ? "Выполняется действие с прежним коннектором…" :
            status.Transferred ? "Компьютер переведён на Платформа Коннектор" +
                (status.TransferredAtUtc is { } at ? $" {at.ToLocalTime():dd.MM.yyyy HH:mm}." : ".") :
            status.Incomplete ? "Перевод не завершён. Нажмите «Вернуть прежний коннектор»." :
            _runtimeServices.CommonFolders.LegacyConnectorSnapshot is { } legacy
                ? legacy.NeighbourModeActive
                    ? "Папку BIM_Models на этом компьютере обслуживает прежний Structura Connector."
                    : "Прежний Structura Connector на этом компьютере не обнаружен."
                : "Компьютер не переведён на Платформа Коннектор.";
        return new
        {
            status = text,
            transferred = status.Transferred,
            canTransfer = !busy && !status.Transferred && !status.Incomplete,
            canRestore = !busy && status.CanRestore
        };
    }

    /// <summary>
    /// While transferred, an old client started by hand gets one notice per process (no automatic
    /// close): its 1219 handling removes every connection to the server, including this connector's drive.
    /// </summary>
    private async Task WatchLegacyConnectorAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(LegacyWatchInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                IReadOnlyList<int> started;
                try { started = _runtimeServices.LegacyTransfer.TakeNewlyStartedLegacyProcesses(); }
                catch (Exception) { continue; }
                var previous = _legacyStatus;
                _legacyStatus = ReadLegacyStatus();
                if (started.Count == 0)
                {
                    if (previous != _legacyStatus) PublishSnapshot();
                    continue;
                }
                lock (_gate) _notice = Notice("legacy-started", LegacyStartedMessage, "warn");
                _connectorHost.Log("Legacy connector process started after the transfer; notice shown.");
                try { _legacyStartedNotice?.Invoke(LegacyStartedMessage); }
                catch (Exception) { /* The in-app notice remains. */ }
                PublishSnapshot();
            }
        }
        catch (OperationCanceledException) { }
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

        if (input is null) throw UserError("Сначала выберите исходный файл в системном диалоге.");
        if (string.IsNullOrWhiteSpace(output)) throw UserError("Сначала выберите папку результата.");

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
            _fields[target] = path;
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
            throw UserError("Выберите не более 128 IFC-файлов.");
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
            throw UserError("Выбранный файл не соответствует ожидаемому типу.");
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
            throw UserError("Выбранная папка недоступна.");
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
        if (!asyncCommand.CanExecute(null)) throw UserError("Действие сейчас недоступно.");
        StartOperation(() => asyncCommand.ExecuteAsync(), name, cancellationToken);
    }

    private void StartOperation(Func<Task> operation, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lifetime.IsCancellationRequested) throw new OperationCanceledException(_lifetime.Token);
        lock (_gate) _notice = Notice(name, "Действие запущено.", "progress");
        Task task = ObserveAsync(operation, name);
        TrackOperation(task);
    }

    private void TrackOperation(Task task)
    {
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
            // The module's own Russian result text is more precise than the generic outcome line.
            var detail = name == "sharing-config" && outcome is OperationOutcome.Rejected or OperationOutcome.Failed
                ? ConnectorJobJournal.StepText(_modelSharing?.ResultMessage) : null;
            lock (_gate)
            {
                _notice = _operationResults.Remove(name, out var own)
                    ? Notice(name, own.Message, own.Kind)
                    : Notice(name, detail ?? OutcomeMessage(outcome, name), OutcomeKind(outcome));
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate) _notice = Notice(name, "Действие отменено.", "info");
        }
        catch (ConnectorSignInException ex)
        {
            lock (_gate) _notice = Notice(name, ex.Message, "error");
        }
        catch (GraphiteUserException ex)
        {
            lock (_gate) _notice = Notice(name, ex.Message, "error");
        }
        catch (CommonSmbAccessException ex) when (ex.Code == "smb_credential_conflict")
        {
            // The foreign Windows connection is left untouched; its account is never logged.
            lock (_gate) _notice = Notice(name, "Этот сервер уже подключён в Windows под другой учётной записью " +
                "(например, прежним Structura Connector). Откройте папку через прежний коннектор. " +
                "Чтобы работать через Платформа Коннектор, выполните в настройках «Перевести компьютер на " +
                "Платформа Коннектор».", "error");
            _connectorHost.Log($"Graphite native {name} failed ({ex.GetType().Name}, {ex.Code}, " +
                $"win32 {(ex.InnerException as Win32Exception)?.NativeErrorCode}).");
        }
        catch (CommonSmbAccessException ex) when (ex.Code == "smb_unmount_not_confirmed")
        {
            lock (_gate) _notice = Notice(name, "Отключение диска отменено.", "info");
        }
        catch (CommonSmbAccessException ex) when (ex.Code == "smb_legacy_client_active")
        {
            // Neighbour mode: no native mount was attempted and the previous connector is untouched.
            lock (_gate) _notice = Notice(name, "Папка BIM_Models на этом компьютере открывается через " +
                "прежний Structura Connector. Чтобы открывать её здесь, выполните в настройках «Перевести компьютер " +
                "на Платформа Коннектор».", "warn");
            _connectorHost.Log($"Graphite native {name} skipped ({ex.Code}; " +
                $"{_runtimeServices.CommonFolders.LegacyConnectorSnapshot?.ToSafeDiagnostic()}).");
        }
        catch (Exception ex)
        {
            lock (_gate) _notice = Notice(name, "Действие не выполнено. Подробности доступны в журнале.", "error");
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

    private static string OutcomeKind(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Cancelled => "info",
        OperationOutcome.Rejected or OperationOutcome.Failed => "error",
        _ => "ok"
    };

    private static string OutcomeMessage(OperationOutcome outcome, string command) => outcome switch
    {
        OperationOutcome.Succeeded => "Готово.",
        OperationOutcome.Cancelled => "Действие отменено.",
        OperationOutcome.Rejected when command is "ifc-apply" or "ifc-rollback" => "Закройте Tekla Structures и повторите действие.",
        OperationOutcome.Rejected when command is "sharing-config" => "Model Sharing не настроен. Проверьте условия на вкладке и повторите.",
        OperationOutcome.Rejected => "Действие недоступно. Проверьте подключение и состояние приложения.",
        OperationOutcome.Failed => "Действие не выполнено. Подробности доступны в журнале.",
        _ => "Состояние обновлено."
    };

    private static void Execute(ICommand command)
    {
        if (!command.CanExecute(null)) throw UserError("Действие сейчас недоступно.");
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
        if (_converterWorkspace.IsSubmitting) throw UserError("Дождитесь завершения задания.");
        // «Очистить завершённые»: a queued or running job stays in the list.
        foreach (ConverterWorkspaceJob job in _converterWorkspace.Jobs
                     .Where(job => GraphiteCommandRules.IsFinishedJobState(job.State)).ToArray())
        {
            if (ReferenceEquals(_converterWorkspace.SelectedJob, job)) _converterWorkspace.SelectedJob = null;
            _converterWorkspace.Jobs.Remove(job);
        }
    }

    private void SelectRequestedJob(JsonElement payload)
    {
        string id = ReadString(payload, "target");
        if (string.IsNullOrWhiteSpace(id)) return;
        ConverterWorkspaceJob? job = _converterWorkspace.Jobs.FirstOrDefault(candidate => candidate.RequestId == id);
        if (job is not null) _converterWorkspace.SelectedJob = job;
    }

    private ConverterJobFacts ConverterFacts(ConverterWorkspaceJob job)
    {
        var (input, output) = ConverterSizes(job);
        return new ConverterJobFacts(
            job.RequestId,
            job.ExecutorId is ConverterWorkspaceViewModel.IfcOptimizeExecutor or ConverterWorkspaceViewModel.IfcAnalyzeExecutor ? "ifc" : "fbx",
            job.Operation == ConverterJobOperation.Analyze ? "analyze" : "optimize",
            job.State,
            job.Progress,
            job.Product == ConnectorProductId.Platform ? "platform" : "structura",
            SafeResultName(job.InputPath),
            SafeResultName(job.OutputPath),
            !string.IsNullOrWhiteSpace(job.OutputPath),
            !string.IsNullOrWhiteSpace(job.ReportPath),
            job.Operation == ConverterJobOperation.Optimize && !string.IsNullOrWhiteSpace(job.ReportPath),
            input, output);
    }

    /// <summary>Sizes before and after: the source file and the published result; read once per finished job.</summary>
    private (long? Input, long? Output) ConverterSizes(ConverterWorkspaceJob job)
    {
        lock (_gate)
            if (_converterSizes.TryGetValue(job.RequestId, out var cached)) return cached;
        static long? Size(string path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? new FileInfo(path).Length : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        }
        var sizes = (Size(job.InputPath), Size(job.OutputPath));
        if (GraphiteCommandRules.IsFinishedJobState(job.State)) lock (_gate) _converterSizes[job.RequestId] = sizes;
        return sizes;
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

    private void OnRuntimeJobPicked(ConnectorJobEnvelope job)
    {
        try { Journal.Admit(job); }
        catch (Exception ex) { _connectorHost.Log($"Graphite job journal admission failed ({ex.GetType().Name})."); }
    }

    private void OnRuntimeJobStatus(ConnectorJobStatusEnvelope status)
    {
        try { Journal.Observe(status); }
        catch (Exception ex) { _connectorHost.Log($"Graphite job journal update failed ({ex.GetType().Name})."); }
    }

    /// <summary>«Проверить обновления» without native dialogs: the outcome is the notice of the command.</summary>
    private async Task CheckUpdatesAsync()
    {
        var before = _connectorHost.UpdateInfo.CheckedAtUtc;
        await _connectorHost.CheckUpdatesAsync(showDialogs: false);
        var info = _connectorHost.UpdateInfo;
        if (info.CheckFailed) throw UserError("Не удалось проверить обновления. Проверьте подключение к интернету.");
        if (info.CheckedAtUtc == before) throw UserError("Проверка обновлений сейчас недоступна для этой установки.");
        lock (_gate) _operationResults["update-check"] = _connectorHost.HasPendingUpdate
            ? ("Доступна новая версия" + (DesktopUpdateNotes.SafeVersion(info.Version) is { } version ? " " + version : "") + ".", "ok")
            : ("Установлена актуальная версия.", "info");
    }

    /// <summary>Runs a module command as a local journal entry; the module's own outcome decides its result.</summary>
    private void StartJournaledCommand(ICommand command, string name, string kind, string title, string firstStep,
        Func<OperationOutcome> outcome, Func<string> resultMessage, CancellationToken cancellationToken)
    {
        if (command is not AsyncRelayCommand asyncCommand)
            throw new InvalidOperationException("Команда не поддерживает наблюдаемое async-выполнение.");
        if (!asyncCommand.CanExecute(null)) throw UserError("Действие сейчас недоступно.");
        StartOperation(async () =>
        {
            var progress = Journal.BeginLocal(kind, title, "Tekla Structures", firstStep);
            try
            {
                await asyncCommand.ExecuteAsync();
            }
            catch (Exception)
            {
                progress.Fail("Действие не выполнено. Подробности доступны в журнале.");
                throw;
            }
            var result = outcome();
            // Declined in the page dialog: nothing was done, so the journal keeps no entry.
            if (result is OperationOutcome.Cancelled or OperationOutcome.None) { progress.Discard(); return; }
            var text = ConnectorJobJournal.StepText(resultMessage()?.Split('\n', 2)[0]);
            if (result == OperationOutcome.Succeeded) progress.Succeed(text);
            else progress.Fail(text ?? OutcomeMessage(result, name));
        }, name, cancellationToken);
    }

    /// <summary>
    /// Files dropped on the window: a user selection like the picker's, validated by the same rules (fully
    /// qualified existing files of the expected type, at most 128 IFC files or one ZIP/folder for FBX).
    /// </summary>
    private void DropInput(string target, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) throw UserError("Перетащите файлы из проводника.");
        switch (target)
        {
            case "ifc-input":
            {
                if (paths.Count > 128 || paths.Any(path => !Path.IsPathFullyQualified(path) || !File.Exists(path) ||
                        !string.Equals(Path.GetExtension(path), ".ifc", StringComparison.OrdinalIgnoreCase)))
                    throw UserError("Перетащите не более 128 IFC-файлов.");
                lock (_gate)
                    foreach (string path in paths)
                        if (!_ifcFiles.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)))
                            _ifcFiles.Add(NativeFile.FromPath(path));
                break;
            }
            case "zip-input":
            {
                var path = paths.Count == 1 ? paths[0] : null;
                var zip = path is not null && Path.IsPathFullyQualified(path) && File.Exists(path) &&
                          string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);
                var folder = path is not null && Path.IsPathFullyQualified(path) && Directory.Exists(path);
                if (!zip && !folder) throw UserError("Перетащите один ZIP-архив или одну папку с FBX.");
                lock (_gate) { _files.Clear(); _files.Add(NativeFile.FromPath(path!)); }
                break;
            }
            default:
                throw Unavailable("Неизвестный тип выбора файла.");
        }
    }

    /// <summary>Tekla as Bridge.Desktop reported it at the last check: version, open model, bridge address and time.</summary>
    private object TeklaSnapshot()
    {
        var info = _platformTools.TeklaInfo;
        return new
        {
            connected = info?.Connected == true,
            version = info?.TeklaVersion,
            model = info?.ModelName,
            bridgeVersion = info?.BridgeVersion,
            bridgeAddress = new Uri(global::Platform.Cad.Tekla.Adapter.TeklaProviderAdapter.DefaultBridgeBaseUrl).Authority,
            checkedAt = _platformTools.TeklaCheckedAtUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    /// <summary>«О программе»: version and build of this package and versions of its components.</summary>
    private object AboutSnapshot()
    {
        lock (_gate)
        {
            if (!_webViewVersionRead)
            {
                _webViewVersionRead = true;
                try { _webViewVersion = DesktopUpdateNotes.SafeVersion(Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString()); }
                catch (Exception) { _webViewVersion = null; }
            }
            if (!_ifcEngineVersionRead)
            {
                _ifcEngineVersionRead = true;
                _ifcEngineVersion = ReadIfcEngineVersion();
            }
        }
        return new
        {
            version = ApplicationVersion,
            build = ApplicationBuild,
            netbird = _commonAccess.LastOverlay?.DaemonVersion,
            webView2 = _webViewVersion,
            ifcEngine = _ifcEngineVersion
        };
    }

    /// <summary>Version of the bundled IFC engine from the verified engine manifest written at packaging.</summary>
    private string? ReadIfcEngineVersion()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "workers", "engine-manifest.json");
        try
        {
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > 64 * 1024) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return document.RootElement.TryGetProperty("ifcOptimizer", out var engine) &&
                   engine.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                ? DesktopUpdateNotes.SafeVersion(version.GetString())
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _connectorHost.Log($"Graphite: IFC engine manifest unavailable ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>The page title bar cannot move the window (WebView2 without non-client regions): the system caption is used.</summary>
    public void UseNativeCaption()
    {
        _nativeCaption = true;
        PublishSnapshot();
    }

    /// <summary>Version, archive size and source host of the pinned Blender runtime, read once from its bundled manifest.</summary>
    private object? AgrRuntimeFacts()
    {
        lock (_gate)
        {
            if (_agrRuntimeRead) return _agrRuntime;
            _agrRuntimeRead = true;
            try
            {
                var manifest = global::Platform.Connector.Compute.Agr.AgrBlenderRuntimeManifest.Load(_platformTools.NativeApi.AgrRuntimeManifestPath);
                _agrRuntime = new { version = manifest.Version, archiveSizeBytes = manifest.ArchiveSizeBytes, sourceHost = manifest.ArchiveUrl.Host };
            }
            catch (Exception ex)
            {
                _connectorHost.Log($"Graphite: Blender runtime manifest unavailable ({ex.GetType().Name}).");
            }
            return _agrRuntime;
        }
    }

    private void OnOwnerWindowStateChanged(object? sender, EventArgs args)
    {
        var maximized = _connectorHost.OwnerWindow.WindowState == System.Windows.WindowState.Maximized;
        if (maximized == _windowMaximized) return;
        _windowMaximized = maximized;
        PublishSnapshot();
    }

    /// <summary>Window buttons of the page title bar; the change is queued so the reply is not re-entrant.</summary>
    private void OnOwnerWindow(Action<System.Windows.Window> action)
    {
        var window = _connectorHost.OwnerWindow;
        window.Dispatcher.BeginInvoke(new Action(() => action(window)));
    }

    private void SaveTheme(string requested)
    {
        var theme = GraphiteCommandRules.NormalizeTheme(requested) ?? throw UserError("Неизвестная тема оформления.");
        lock (_gate) _uiTheme = theme;
        TrackOperation(SaveThemeAsync(theme));
    }

    private async Task SaveThemeAsync(string theme)
    {
        try
        {
            await _connectorHost.SaveDesktopPreferencesAsync(
                new DesktopPreferences(null, null, null, null, null, null, null, null, UiTheme: theme));
        }
        catch (Exception ex)
        {
            lock (_gate) _notice = Notice("ui-theme", "Тема применена, но не сохранена. Подробности доступны в журнале.", "error");
            _connectorHost.Log($"Graphite theme save failed ({ex.GetType().Name}).");
            PublishSnapshot();
        }
    }

    private void OpenTeklaFolder(string command)
    {
        Dictionary<string, string> picked;
        lock (_gate) picked = new Dictionary<string, string>(_pickedFolders, StringComparer.Ordinal);
        OpenWithShell(GraphiteCommandRules.ResolveTeklaFolder(command, picked, _connectorHost.Settings),
            "Не удалось открыть папку в проводнике.");
    }

    private void OpenWithShell(string target, string failure)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _connectorHost.Log($"Graphite shell open failed ({ex.GetType().Name}).");
            throw UserError(failure);
        }
    }

    private SafeNotice Notice(string command, string message, string kind, long? generation = null) =>
        new(command, message, kind, Interlocked.Increment(ref _noticeSeq), generation);
    private void OnPlatformSnapshotChanged(ConnectorRuntimeSnapshot snapshot) => PublishSnapshot();
    private void OnCommonOperationChanged() => PublishSnapshot();
    private void OnCommonRecoveryChanged()
    {
        lock (_gate) if (_notice?.ConnectionGeneration is not null) _notice = null;
        PublishSnapshot();
    }
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
        _confirmations.Dispose();
        _runtimeServices.Host.JobPicked -= OnRuntimeJobPicked;
        _runtimeServices.Host.JobStatusChanged -= OnRuntimeJobStatus;
        Journal.Changed -= PublishSnapshot;
        _confirmations.Changed -= PublishSnapshot;
        await _runtimeServices.CommonOperations.DisposeAsync();
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
        _runtimeServices.CommonOperations.Changed -= OnCommonOperationChanged;
        _runtimeServices.CommonRecoveryChanged -= OnCommonRecoveryChanged;
        _connectorHost.OwnerWindow.StateChanged -= OnOwnerWindowStateChanged;
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

    private static GraphiteUserException Unavailable(string message) => new(message);
    private static GraphiteUserException UserError(string message) => new(message);
    private static string Allowed(string value, HashSet<string> allowed, string fallback) => allowed.Contains(value) ? value : fallback;
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
    /// <summary>Short source revision of this build (the «+sha» suffix of the informational version), when present.</summary>
    private static string? ResolveApplicationBuild()
    {
        var parts = typeof(GraphiteDesktopController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+', 2);
        var build = parts is { Length: 2 } ? parts[1].Trim() : null;
        return build is { Length: >= 7 } && build.All(char.IsAsciiHexDigit) ? build[..7].ToLowerInvariant() : null;
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

    /// <summary>Kind: ok, error, warn, info or progress; Seq lets the document show each notice exactly once.</summary>
    private sealed record SafeNotice(string Command, string Message, string Kind, long Seq, long? ConnectionGeneration = null);
}
