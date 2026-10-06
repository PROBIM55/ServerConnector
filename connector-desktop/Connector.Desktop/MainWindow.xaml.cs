using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Connector.Desktop.Features.Connector;
using Connector.Desktop.Features.Tekla.Standard;
using Connector.Desktop.Models;
using Connector.Desktop.Services;
using Forms = System.Windows.Forms;

namespace Connector.Desktop;

public partial class MainWindow : Window, IShellHost, IConnectorHost
{
    private const string FixedServerUrl = "https://server.structura-most.ru";
    private const string FixedUpdateManifestUrl = "https://server.structura-most.ru/updates/latest.json";
    private const int FixedHeartbeatSeconds = 60;
    private const string DefaultSmbSharePath = @"\\62.113.36.107\BIM_Models";
    // Tekla defaults: still used by the shell-owned settings bootstrap/persistence (LoadSettingsToUi, ReadSettingsFromUi,
    // ConnectByTokenInternalAsync). The lifted StandardView keeps its own private copies for its UI; these stay here
    // because the connector's settings layer references them independently of the Стандарт module.
    private const string FixedTeklaStandardManifestUrl = "https://server.structura-most.ru/updates/tekla/firm/latest.json";
    private const string FixedTeklaExtensionsManifestUrl = "https://server.structura-most.ru/updates/tekla/extensions/latest.json";
    private const string FixedTeklaLibrariesManifestUrl = "https://server.structura-most.ru/updates/tekla/libraries/latest.json";
    private const string DefaultTeklaStandardLocalPath = @"C:\Company\TeklaFirm";
    private const string DefaultTeklaExtensionsLocalPath = @"C:\TeklaStructures\2025.0\Environments\common\Extensions";
    private static readonly string DefaultTeklaLibrariesLocalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Grasshopper",
        "Libraries");
    private const string DefaultTeklaPublishSourcePath = @"\\62.113.36.107\BIM_Models\Tekla\02_ПАПКА ФИРМЫ\01_XS_FIRM";
    private const string DefaultTeklaExtensionsPublishSourcePath = @"\\62.113.36.107\BIM_Models\Tekla\02_ПАПКА ФИРМЫ\07_Extensions";
    private const string DefaultTeklaLibrariesPublishSourcePath = @"\\62.113.36.107\BIM_Models\Tekla\02_ПАПКА ФИРМЫ\02_Grasshopper\Libraries\8";

    private readonly SettingsService _settingsService;
    private readonly IAutoStartService _autoStartService;
    private readonly HeartbeatClient _heartbeatClient = new(new HttpClient { Timeout = TimeSpan.FromSeconds(110) });
    private readonly UpdateService _updateService = new(new HttpClient { Timeout = TimeSpan.FromSeconds(40) });
    private readonly PackageUpdateService _packageUpdateService;
    private readonly TeklaStandardService _teklaStandardService;
    private readonly TcpConnectivityProbe _tcpConnectivityProbe = new();
    // Model Sharing / VPN services now live inside their feature modules (the shell catalog). See ComposeFeatureModules().
    private readonly Shell.ShellViewModel _shell;   // modular feature catalog (domains → modules); assigned in ctor (needs `this` as IShellHost)
    private readonly ConnectorRuntimeServices _runtimeServices;
    private readonly Features.ConverterWorkspace.ConverterWorkspaceModule _converterWorkspace;
    private readonly Features.PlatformTools.PlatformToolsModule _platformTools;
    private readonly GraphiteDesktopController _graphite;
    private StandardModule? _standard;   // Tekla domain → "Стандарт" module (the lifted firm/extensions/libraries sync engine)
    private ConnectorModule? _connector;   // "Коннектор" domain module (the lifted login/heartbeat FRONT-END view); engine stays here
    private string _lastVpnConfig = string.Empty;   // last config delivered by bootstrap (kept in memory, not persisted)
    private string _lastSmbLogin = string.Empty;    // SMB creds from bootstrap, for mounting the share over VPN
    private string _lastSmbPassword = string.Empty;
    private string _connectorManagedSmbShareRoot = string.Empty;
    private string _connectorManagedSmbDrive = string.Empty;
    private readonly ManagedSmbMappingStore _managedSmbMappings = new();
    private readonly DesktopBackgroundAgent _backgroundAgent;
    private readonly Forms.NotifyIcon _trayIcon;
    private static readonly IReadOnlyList<ReleaseNoteItem> ReleaseNotes = new List<ReleaseNoteItem>
    {
        new()
        {
            Version = "1.0.29",
            PublishedAt = "31.07.2026",
            Title = "VPN включается автоматически",
            Changes = new[]
            {
                "После подключения по токену Connector сам устанавливает и включает VPN; пользователю нужно только один раз подтвердить UAC",
                "Если актуальный VPN уже работает, Connector использует его без переустановки и повторных запросов прав администратора",
                "SMB, heartbeat, обновления, синхронизация и Model Sharing используют защищённый маршрут к серверу по умолчанию",
                "Исправлено восстановление VPN-конфигурации после перезапуска и состояние кнопок управления VPN"
            }
        },
        new()
        {
            Version = "1.0.28",
            PublishedAt = "31.07.2026",
            Title = "Надёжное подключение общей папки",
            Changes = new[]
            {
                "Коннектор быстро определяет блокировку прямого SMB (TCP 445) и не ждёт долгого тайм-аута Windows",
                "Если VPN уже включён, общая папка автоматически подключается через VPN",
                "После первого включения VPN общая папка подключается автоматически"
            }
        },
        new()
        {
            Version = "1.0.27",
            PublishedAt = "31.07.2026",
            Title = "Надёжность общей папки и обновлений",
            Changes = new[]
            {
                "Исправлено подключение к общей папке: Connector больше не отключает другие сетевые диски пользователя при конфликте SMB-учётных данных",
                "Очистка конфликтующего подключения теперь ограничена только сервером общей папки; если Windows удерживает сессию, Connector показывает безопасную точечную инструкцию",
                "Установщик обновления скачивается только по HTTPS и запускается только после успешной проверки SHA-256"
            }
        },
        new()
        {
            Version = "1.0.26",
            PublishedAt = "22.06.2026",
            Title = "Ключевые изменения версии",
            Changes = new[]
            {
                "Добавлена вкладка «Общая папка (VPN)»: доступ к общей папке можно открыть через защищённый VPN-канал, если прямое SMB-подключение блокируется сетью",
                "VPN теперь доступен всем подключённым устройствам; конфигурация создаётся автоматически при подключении по токену",
                "Исправлено открытие общей папки через VPN: папка монтируется с выданными сервером SMB-учётными данными",
                "Добавлен раздел Tekla «Патчинг»: коннектор поставляет актуальный патч IFC-экспорта для Tekla 2025 SP7 и может установить его с резервной копией",
                "Интерфейс коннектора переведён на модульную структуру: разделы Tekla, Model Sharing, VPN, Structura и Атрибуты разделены на самостоятельные вкладки",
                "Безопасность: при отзыве доступа устройства его учётная запись общей папки отключается, VPN-peer удаляется, а активные подключения разрываются"
            }
        },
        new()
        {
            Version = "1.0.22",
            PublishedAt = "02.06.2026",
            Title = "Ключевые изменения версии",
            Changes = new[]
            {
                "Подключение больше не прерывается, если недоступна общая SMB-папка (например, провайдер закрывает порт 445): коннектор продолжает работать, heartbeat и Model Sharing включаются",
                "Сообщение о неподключённой SMB-папке стало понятнее (возможные причины и что это не влияет на Model Sharing)"
            }
        },
        new()
        {
            Version = "1.0.21",
            PublishedAt = "01.06.2026",
            Title = "Ключевые изменения версии",
            Changes = new[]
            {
                "Добавлен раздел Model Sharing: одной кнопкой Connector готовит Tekla на этом компьютере к совместной работе над моделями через сервер фирмы",
                "Настройка выполняется локально, без входа в Trimble; пользователь определяется по токену устройства",
                "Connector сам определяет папку Tekla и сохраняет резервную копию исходного файла; настройку можно повторить после обновления Tekla",
                "Выпуск предназначен для ручной установки и проверки"
            }
        },
        new()
        {
            Version = "1.0.20",
            PublishedAt = "16.04.2026",
            Title = "Ключевые изменения версии",
            Changes = new[]
            {
                "Исправлен сценарий обновления Connector поверх установленной предыдущей версии",
                "Устранена проблема, из-за которой после неудачного обновления приложение могло перестать запускаться",
                "Выпуск предназначен для ручной установки и проверки"
            }
        },
        new()
        {
            Version = "1.0.19",
            PublishedAt = "16.04.2026",
            Title = "Ключевые изменения версии",
            Changes = new[]
            {
                "Коннектор теперь понятнее показывает, какой именно файл не удалось обновить и какой процесс, вероятнее всего, его блокирует",
                "Сообщения об ошибках синхронизации выводятся не только в журнал, но и в окно приложения",
                "Проверка обновлений Tekla Sync выполняется чаще, чтобы изменения у пользователей подтягивались быстрее"
            }
        },
        new()
        {
            Version = "1.0.18",
            PublishedAt = "16.04.2026",
            Title = "Ключевые изменения версии",
            Changes = new[]
            {
                "Исправлена установка встроенного git в Connector",
                "Если встроенный git отсутствует после установки, коннектор теперь сам восстанавливает его из локального пакета",
                "Повышена надежность первой синхронизации на новом рабочем компьютере"
            }
        },
        new()
        {
            Version = "1.0.17",
            PublishedAt = "16.04.2026",
            Title = "Ключевые изменения версии",
            Changes = new[]
            {
                "Повышена надежность синхронизации папки фирмы, пользовательских приложений и Grasshopper Libraries",
                "Коннектор корректнее восстанавливает локальные данные синхронизации после сбоев",
                "Улучшена диагностика ошибок при применении обновлений на рабочем компьютере пользователя"
            }
        },
        new()
        {
            Version = "1.0.16",
            PublishedAt = "16.04.2026",
            Title = "Ключевые изменения версии",
            Changes = new[]
            {
                "Повышена стабильность синхронизации папки фирмы, пользовательских приложений и Grasshopper Libraries",
                "Коннектор корректнее восстанавливает локальные данные синхронизации и повторно получает обновления с сервера",
                "Улучшена надежность применения обновлений на рабочем компьютере пользователя"
            }
        },
        new()
        {
            Version = "1.0.15",
            PublishedAt = "13.04.2026",
            Title = "Ключевые изменения версии",
            Changes = new[]
            {
                "Исправлена синхронизация папки фирмы после изменения структуры файлов в Git",
                "Для папки фирмы сохранен строгий режим: лишние файлы удаляются, нужные файлы обновляются по эталону",
                "Повышена стабильность применения обновлений: корректно обрабатываются файлы и папки с атрибутом ReadOnly"
            }
        },
        new()
        {
            Version = "1.0.14",
            PublishedAt = "11.04.2026",
            Title = "Ключевые изменения версии",
            Changes = new[]
            {
                "Исправлена синхронизация папки фирмы после изменения структуры стандартов в Git. Обновление снова корректно приводит локальную папку фирмы к актуальному эталону",
                "Обновлена логика синхронизации папок Tekla. Коннектор теперь в любом случае пытается применить обновления для папки фирмы, пользовательских приложений и Grasshopper Libraries, даже если в этот момент запущены Tekla или Rhino",
                "Если обновление не удалось применить в одной из папок из-за занятых файлов, коннектор останавливает обновление только для этой папки и продолжает проверку остальных разделов",
                "Сообщения о проблемах стали понятнее. Теперь коннектор отдельно показывает, что именно помешало обновлению: запущенная Tekla, запущенный Rhino или занятый файл, открытый другой программой",
                "Ручная синхронизация остается доступной для тех случаев, когда часть файлов не удалось обновить автоматически и их нужно подтянуть после закрытия блокирующей программы"
            }
        },
        new()
        {
            Version = "1.0.13",
            PublishedAt = "11.04.2026",
            Title = "Ключевые изменения версии",
            Changes = new[]
            {
                "Добавлена синхронизация Extensions и Grasshopper Libraries через коннектор. Ранее через коннектор синхронизировалась только папка фирмы, теперь по тому же принципу можно централизованно обновлять и пользовательские приложения Tekla, и общие библиотеки Grasshopper",
                "Принцип синхронизации теперь разделен по типам папок. Папка фирмы приводится в точное соответствие опубликованному стандарту, а для Extensions и Grasshopper Libraries коннектор добавляет и обновляет только управляемые файлы, не удаляя локальные файлы пользователя, которых нет в общем контуре",
                "Синхронизация стала автоматической. Коннектор сам проверяет обновления и сам применяет их без лишних ручных действий. Если обновление не удалось применить из-за занятых файлов, коннектор сообщает об этом понятным текстом и предлагает повторить синхронизацию после освобождения файлов",
                "Раздел Стандарт Tekla переработан. Теперь папка фирмы, пользовательские приложения и Grasshopper Libraries вынесены в отдельные вкладки, а пути для каждой папки можно настраивать отдельно под конкретный компьютер и версию Tekla",
                "Для ответственных за обновление стандарта добавлена единая публикация изменений по трем разделам из одного окна, с последовательным запуском и понятным отображением результата",
                "Добавлена вкладка Structura. В одном месте собраны быстрые переходы к Speckle и Nextcloud, а также окно с доступами, где можно удобно посмотреть и скопировать домен, логин и пароль",
                "Добавлено окно прогресса для длительных операций. Во время синхронизации и публикации теперь видно, что именно делает коннектор и на каком этапе находится процесс",
                "Улучшены статусы и уведомления. Коннектор понятнее показывает, что именно требует обновления, какие действия выполняются автоматически и когда нужно вмешательство пользователя",
                "Уведомление о новой версии теперь показывается заметнее и остается на экране, пока пользователь не закроет его сам",
                "Добавлен раздел Что нового. Теперь ключевые изменения по версиям можно посмотреть прямо в коннекторе"
            }
        }
    };

    private AppSettings _settings = new();
    private bool _isRunning;
    private bool _allowClose;
    private bool _trayHintShown;
    private string _activeSessionId = string.Empty;
    private UpdateManifest? _pendingUpdate;
    private PackageUpdateCandidate? _pendingPackageUpdate;
    private UpdateToastWindow? _updateToastWindow;
    private string? _downloadedInstallerPath;
    private bool _updateOfferShown;
    private string _lastUpdateToastVersion = string.Empty;
    private bool _updateCheckInProgress;
    private bool _teklaCheckInProgress;   // seam backing store for IShellHost.TeklaCheckInProgress (read by connect flow)
    private bool _teklaBalloonShown;      // seam backing store; reset via IShellHost.ResetTeklaPendingBalloon
    private bool _serverConnectionFailed;
    private readonly bool _allowOwnedFixtureClose;
    private readonly bool _runLoadedExternalActions;
    private readonly string? _webViewUserDataDirectory;
    private readonly Action? _loadedInitializationCompleted;
    private Task? _disposeResourcesTask;
    private Task? _deferredResourcesDisposeTask;
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TeklaSyncCheckInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BackgroundShutdownDrainTimeout = TimeSpan.FromSeconds(3);

    public MainWindow() : this(MainWindowServices.CreateDefault())
    {
    }

    public MainWindow(MainWindowServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _settingsService = services.Settings;
        _autoStartService = services.AutoStart;
        _runtimeServices = services.Runtime;
        _teklaStandardService = services.WindowTeklaStandard;
        _packageUpdateService = services.PackageUpdates;
        _allowOwnedFixtureClose = services.AllowOwnedFixtureClose;
        _runLoadedExternalActions = services.RunLoadedExternalActions;
        _webViewUserDataDirectory = services.WebViewUserDataDirectory;
        _loadedInitializationCompleted = services.LoadedInitializationCompleted;
        InitializeComponent();
        _shell = new Shell.ShellViewModel(this, this, _runtimeServices.Platform, _runtimeServices.LegacyPartConverter,
            services.ShellTeklaStandard, services.VpnProvisioning, services.ModelSharingProvisioning, services.IfcExportPatch);
        _converterWorkspace = new Features.ConverterWorkspace.ConverterWorkspaceModule(
            _runtimeServices.ConverterClient, action => Dispatcher.BeginInvoke(action));
        _platformTools = new Features.PlatformTools.PlatformToolsModule();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ComposeFeatureModules();   // bind Tekla sub-tabs (TeklaTabs) + host Structura/VPN/Атрибуты module views + wire glue
        _backgroundAgent = new DesktopBackgroundAgent(
            TimeSpan.FromSeconds(FixedHeartbeatSeconds),
            UpdateCheckInterval,
            TeklaSyncCheckInterval,
            isLegacyMode: () => !_runtimeServices.CommonAccess.IsSelected,
            heartbeat: RunHeartbeatTickAsync,
            appUpdate: RunUpdateTickAsync,
            teklaSync: RunTeklaSyncTickAsync,
            onError: ReportBackgroundAgentError);
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        _trayIcon = CreateTrayIcon(services.TrayIconVisible);
        LoadSettingsToUi();
        UpdateRunStateUi();
        UpdateActionButtonUi();
        _standard?.RefreshUi();
        SyncFeatureModules();
        UpdateHeaderStatusUi();
        _graphite = new GraphiteDesktopController(this, _runtimeServices, _shell,
            _platformTools.ViewModel, _converterWorkspace.ViewModel,
            confirmFolderDisconnect: drive => Dispatcher.InvokeAsync(() =>
                ThemedDialogs.Show(this, $"Отключить общую папку от диска {drive}?",
                    "Общая папка", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes).Task);
        _graphite.SnapshotChanged += OnGraphiteSnapshot;
        GraphiteWebHost.Configure(_graphite.HandleAsync, AppendLog, _graphite.CreateDocumentSnapshot);
    }

    private async void OnGraphiteSnapshot(object snapshot)
    {
        if (_exitInProgress) return;
        try { await GraphiteWebHost.PublishSnapshotAsync(snapshot); }
        catch (ObjectDisposedException) { }
        catch (Exception) { AppendLog("Не удалось обновить состояние интерфейса."); }
    }

    // ===== IShellHost (seam for the lifted "Стандарт" module) =========================================
    // Implements the surface the lifted StandardView calls. These map 1:1 onto the shell fields/methods the
    // original MainWindow code touched directly, so the lifted logic stays behaviour-identical.

    // LIVE getter — MainWindow reassigns _settings (ReadSettingsFromUi / ConnectByTokenInternalAsync), never cache.
    AppSettings IShellHost.Settings => _settings;

    void IShellHost.SaveSettings() => _settingsService.Save(_settings);

    HeartbeatClient IShellHost.Heartbeat => _heartbeatClient;

    void IShellHost.Log(string message) => AppendLog(message);

    MessageBoxResult IShellHost.ShowDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        ThemedDialogs.Show(this, message, title, buttons, image);

    Window IShellHost.OwnerWindow => this;

    bool IShellHost.TeklaCheckInProgress
    {
        get => _teklaCheckInProgress;
        set => _teklaCheckInProgress = value;
    }

    // Cross-tab mirror (Коннектор tab) + window header. The module computes the overall status and the action-button
    // presentation and pushes them here at the end of every refresh; the shell applies them to the controls that no
    // longer live in the StandardView (mirrors the old UpdateTeklaUi/UpdateTeklaActionButtonUi/UpdateHeaderStatusUi).
    void IShellHost.OnTeklaStatusChanged(string overallText, System.Windows.Media.Brush overallBrush, string actionButtonContent, bool actionIsSyncStyle, bool inProgress)
    {
        // Cross-tab mirror (ConnectorTeklaSyncStatusTextBlock + ConnectorTeklaSyncButton) moved into ConnectorView;
        // push it through the module. The window header (HeaderFirmStatusTextBlock) stays in the shell.
        _connector?.SetTeklaMirror(overallText, overallBrush, actionButtonContent, actionIsSyncStyle);
        HeaderFirmStatusTextBlock.Text = overallText;
        HeaderFirmStatusTextBlock.Foreground = overallBrush;
    }

    void IShellHost.ShowTrayBalloon(int durationMs, string title, string message, bool isWarning) =>
        _trayIcon.ShowBalloonTip(durationMs, title, message, isWarning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    bool IShellHost.IsWindowVisible => IsVisible && WindowState != WindowState.Minimized;

    void IShellHost.ResetTeklaPendingBalloon() => _teklaBalloonShown = false;

    // ===== IConnectorHost (seam for the lifted "Коннектор" front-end module) ===========================
    // Implements the surface the moved ConnectorView handlers call. The connect/heartbeat ENGINE stays here and is
    // unchanged; these map onto the SACRED engine entry points + the shell primitives the moved handlers touch.
    // Explicit implementations forward to the existing (private) engine methods so their signatures stay untouched.

    // The login/heartbeat SPINE — the moved ConnectByToken_Click calls this VERBATIM engine method.
    Task IConnectorHost.ConnectByTokenAsync(string token, bool showSuccessDialog) =>
        ConnectByTokenInternalAsync(token, showSuccessDialog);

    bool IConnectorHost.IsConnected => _isRunning &&
        !string.IsNullOrWhiteSpace(_activeSessionId) && !_serverConnectionFailed;

    bool IConnectorHost.CanControlBackgroundConnection => true;

    async Task IConnectorHost.StartBackgroundConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = SettingsService.DecryptToken(_settings.TokenCipherBase64);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Сначала подключитесь по токену устройства.");
        if (string.IsNullOrWhiteSpace(_activeSessionId) || _serverConnectionFailed)
            await ConnectByTokenInternalAsync(token, showSuccessDialog: false);
        else
        {
            _backgroundAgent.Start(heartbeatEnabled: true);
            _isRunning = true;
            UpdateRunStateUi();
            await SendHeartbeatSafeAsync();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!((IConnectorHost)this).IsConnected)
            throw new InvalidOperationException("Связь с сервером не подтверждена.");
        UpdateHeaderStatusUi();
    }

    Task IConnectorHost.StopBackgroundConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _backgroundAgent.SetHeartbeatEnabled(false);
        _isRunning = false;
        UpdateRunStateUi();
        UpdateHeaderStatusUi();
        AppendLog("Поддержание связи приостановлено.");
        return Task.CompletedTask;
    }

    Task IConnectorHost.DisconnectAsync()
    {
        _backgroundAgent.SetHeartbeatEnabled(false);
        _isRunning = false;
        _activeSessionId = string.Empty;
        UpdateRunStateUi();
        OnGraphiteSnapshot(_graphite.CreateSnapshot());
        AppendLog("Подключение отключено.");
        return Task.CompletedTask;
    }

    Task IConnectorHost.SavePreferencesAsync(bool autoStart, int heartbeatSeconds)
    {
        if (heartbeatSeconds is < 5 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(heartbeatSeconds));
        _autoStartService.SetEnabled(autoStart);
        _settings.AutoStart = autoStart;
        _settings.HeartbeatSeconds = heartbeatSeconds;
        _settingsService.Save(_settings);
        _backgroundAgent.SetHeartbeatInterval(TimeSpan.FromSeconds(heartbeatSeconds));
        _connector?.LoadFromSettings(SettingsService.DecryptToken(_settings.TokenCipherBase64));
        AppendLog("Настройки сохранены.");
        return Task.CompletedTask;
    }

    Task IConnectorHost.SaveDesktopPreferencesAsync(DesktopPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ApplySelectedFolder(preferences.TeklaFirmLocalPath, value => _settings.TeklaStandardLocalPath = value);
        ApplySelectedFolder(preferences.TeklaExtensionsLocalPath, value => _settings.TeklaExtensionsLocalPath = value);
        ApplySelectedFolder(preferences.TeklaLibrariesLocalPath, value => _settings.TeklaLibrariesLocalPath = value);
        ApplySelectedFolder(preferences.ModelSharingTeklaBin, value => _settings.ModelSharingTeklaBin = value);
        ApplySelectedFolder(preferences.IfcPatchingTeklaBin, value => _settings.IfcPatchingTeklaBin = value);
        ApplySelectedFolder(preferences.IfcPatchingStagingDir, value => _settings.IfcPatchingStagingDir = value);
        ApplySelectedFolder(preferences.ConverterOutputDirectory, value => _settings.ConverterOutputDirectory = value);
        ApplySelectedFolder(preferences.TeklaPublishSourcePath, value => _settings.TeklaPublishSourcePath = value);
        ApplySelectedFolder(preferences.TeklaExtensionsPublishSourcePath, value => _settings.TeklaExtensionsPublishSourcePath = value);
        ApplySelectedFolder(preferences.TeklaLibrariesPublishSourcePath, value => _settings.TeklaLibrariesPublishSourcePath = value);
        if (preferences.AutoStart is { } autoStart)
        {
            _autoStartService.SetEnabled(autoStart);
            _settings.AutoStart = autoStart;
        }
        if (preferences.HeartbeatSeconds is { } heartbeatSeconds)
        {
            if (heartbeatSeconds is < 5 or > 3600) throw new ArgumentOutOfRangeException(nameof(preferences));
            _settings.HeartbeatSeconds = heartbeatSeconds;
            _backgroundAgent.SetHeartbeatInterval(TimeSpan.FromSeconds(heartbeatSeconds));
        }
        _settingsService.Save(_settings);
        _standard?.RefreshUi();
        SyncFeatureModules();
        _connector?.LoadFromSettings(SettingsService.DecryptToken(_settings.TokenCipherBase64));
        AppendLog("Native-настройки сохранены.");
        return Task.CompletedTask;
    }

    Task IConnectorHost.EnsureVpnReadyAsync() => EnsureVpnReadyAsync();

    bool IConnectorHost.CanPublishTekla => _standard is not null && _settings.IsFirmAdmin;

    Task IConnectorHost.ValidateTeklaPublicationAsync(TeklaPublicationRequest request, CancellationToken cancellationToken) =>
        _standard?.ValidatePublicationAsync(request, cancellationToken) ??
            Task.FromException(new NotSupportedException("Публикация Tekla недоступна."));

    Task IConnectorHost.PublishTeklaAsync(TeklaPublicationRequest request, CancellationToken cancellationToken) =>
        _standard?.PublishPublicationAsync(request, cancellationToken) ??
            Task.FromException(new NotSupportedException("Публикация Tekla недоступна."));

    async Task IConnectorHost.MountVpnShareAsync(string drive)
    {
        var expectedUnc = ResolveVpnSmbUnc();
        await EnsureVpnReadyAsync();
        await MountVpnShareToDriveAsync(expectedUnc, drive);
    }

    Task IConnectorHost.UnmountVpnShareAsync(string drive) => DisconnectConnectorManagedSmbAsync(drive);

    async Task IConnectorHost.ExecuteSupportActionAsync(DesktopSupportAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (action)
        {
            case DesktopSupportAction.DownloadPendingUpdate:
                await DownloadPendingUpdateAsync(cancellationToken);
                return;
            case DesktopSupportAction.ShowReleaseNotes:
                new ReleaseNotesWindow(ReleaseNotes, typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "—") { Owner = this }.ShowDialog();
                return;
            case DesktopSupportAction.OpenApplicationJournal:
                ShowApplicationJournal();
                return;
            case DesktopSupportAction.ClearApplicationJournal:
                LogTextBox.Clear();
                return;
            case DesktopSupportAction.OpenLogFolder:
                throw new NotSupportedException("Постоянная папка журналов не ведётся: доступен только текущий журнал приложения.");
            case DesktopSupportAction.ExportDiagnostics:
                ExportDiagnosticsSummary();
                return;
            case DesktopSupportAction.SyncTeklaFirm:
                await RunTeklaSectionSyncAsync(TeklaStandardSection.Firm);
                return;
            case DesktopSupportAction.SyncTeklaExtensions:
                await RunTeklaSectionSyncAsync(TeklaStandardSection.Extensions);
                return;
            case DesktopSupportAction.SyncTeklaLibraries:
                await RunTeklaSectionSyncAsync(TeklaStandardSection.Libraries);
                return;
            default:
                throw new NotSupportedException($"Native-действие {action} недоступно.");
        }
    }

    private void ShowApplicationJournal()
    {
        // The legacy log control is hidden behind the WebView. Keep raw native
        // logs in a native read-only window; never send them through browser IPC.
        var journal = new System.Windows.Controls.TextBox
        {
            Text = LogTextBox.Text, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12,
            Margin = new Thickness(12)
        };
        var dialog = new Window
        {
            Owner = this, Title = "Журнал приложения", Width = 850, Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = journal,
            ShowInTaskbar = false
        };
        dialog.Loaded += (_, _) => journal.ScrollToEnd();
        dialog.ShowDialog();
    }

    private void ExportDiagnosticsSummary()
    {
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "connector-diagnostics.json", Filter = "Диагностика JSON|*.json"
        };
        if (picker.ShowDialog(this) != true) return;
        // An allowlisted summary deliberately excludes raw logs, native paths,
        // device identifiers, configuration, credentials and model contents.
        var summary = new
        {
            schemaVersion = 1, createdUtc = DateTimeOffset.UtcNow,
            appVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3),
            structuraConnected = ((IConnectorHost)this).IsConnected,
            platformConnected = _runtimeServices.Platform.Connection.IsConnected,
            agentRunning = _runtimeServices.Host.IsRunning,
            vpnConfigured = _settings.VpnEnabled && !string.IsNullOrWhiteSpace(_settings.VpnConfigCipherBase64),
            pendingUpdate = ((IConnectorHost)this).HasPendingUpdate
        };
        File.WriteAllText(picker.FileName, System.Text.Json.JsonSerializer.Serialize(summary,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        AppendLog("Диагностическая сводка сохранена.");
    }

    private Task RunTeklaSectionSyncAsync(TeklaStandardSection section) =>
        _standard is null
            ? Task.FromException(new InvalidOperationException("Модуль Стандарт Tekla недоступен."))
            : _standard.RunInteractiveSyncAsync(section);

    private static void ApplySelectedFolder(string? path, Action<string> assign)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var fullPath = path.Trim();
        if (!Path.IsPathFullyQualified(fullPath) || !Directory.Exists(fullPath))
            throw new InvalidOperationException("Выбранная папка недоступна.");
        assign(fullPath);
    }

    private async Task EnsureVpnReadyAsync()
    {
        if (!_settings.VpnEnabled || string.IsNullOrWhiteSpace(_lastVpnConfig))
            throw new InvalidOperationException("VPN доступен после bootstrap с сохранённой native-конфигурацией.");
        if (_shell.Vpn.Module("Общая папка (VPN)") is not Features.Vpn.VpnModule vpn)
            throw new InvalidOperationException("Модуль VPN недоступен.");
        var result = await vpn.EnsureEnabledAsync(showResultDialog: false, openShareOnSuccess: false);
        if (!result.IsSuccess) throw new InvalidOperationException("VPN не подготовлен: " + result.Message);
    }

    // App self-update "check" path (UpdateAction_Click when no pending update).
    Task IConnectorHost.CheckUpdatesAsync(bool showDialogs) => CheckUpdatesAsync(showDialogs);

    // App self-update "install" path (UpdateAction_Click when an update is pending).
    Task IConnectorHost.InstallPendingUpdateAsync(bool confirmBeforeRun) => InstallPendingUpdateAsync(confirmBeforeRun);

    bool IConnectorHost.HasPendingUpdate => _pendingUpdate is not null || _pendingPackageUpdate is not null;

    // Tekla-mirror sync button — identical to the Стандарт-tab button (OperationProgressWindow + "already running").
    Task IConnectorHost.RunTeklaInteractiveSyncAsync() =>
        _standard is null ? Task.CompletedTask : _standard.RunInteractiveSyncAsync();

    void IConnectorHost.Log(string message) => AppendLog(message);

    MessageBoxResult IConnectorHost.ShowDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        ThemedDialogs.Show(this, message, title, buttons, image);

    Window IConnectorHost.OwnerWindow => this;

    void IConnectorHost.SetServerConnectionFailed(bool failed) => _serverConnectionFailed = failed;

    void IConnectorHost.UpdateHeaderStatus() => UpdateHeaderStatusUi();

    // LIVE getter — MainWindow reassigns _settings (ReadSettingsFromUi / ConnectByTokenInternalAsync), never cache.
    AppSettings IConnectorHost.Settings => _settings;

    IReadOnlyList<ReleaseNoteItem> IConnectorHost.ReleaseNotes => ReleaseNotes;

    // (TeklaUpdateAction_Click moved into ConnectorView — the Коннектор-tab mirror button now lives in the module and
    // routes through IConnectorHost.RunTeklaInteractiveSyncAsync.)

    // ===== Feature-module composition (MVVM migration) =================================================
    // The Model Sharing / Structura / VPN tabs now live in self-contained IFeatureModule views hosted from the
    // shell catalog. ComposeFeatureModules() hosts those views once and wires the shell-owned glue (journal,
    // persistence, themed dialogs, the SMB-mount helper). SyncFeatureModules() re-pushes the current settings
    // snapshot into them — at load and after each token-connect — replacing the old per-tab UpdateXxxUi calls.

    private void ComposeFeatureModules()
    {
        ConverterWorkspaceHost.Content = _converterWorkspace.View;
        PlatformToolsHost.Content = _platformTools.View;
        // Capture the Коннектор module + host its view BEFORE LoadSettingsToUi/UpdateRunStateUi/UpdateActionButtonUi
        // (called later in the ctor) push state into it. The connect/heartbeat engine stays here and reaches the
        // moved controls through this module's push methods (replacing the old direct control writes).
        _connector = _shell.Connector.Module("Коннектор") as ConnectorModule;
        ConnectorHost.Content = _connector?.View;

        _standard = _shell.Tekla.Module("Стандарт") as StandardModule;
        // Data-driven Tekla sub-nav: TeklaTabs renders one tab per Tekla module (Стандарт, Model Sharing, Патчинг)
        // from this collection. Adding a Tekla module needs no XAML/host change — only ShellViewModel registration.
        TeklaTabs.ItemsSource = _shell.Tekla.Modules;

        if (_shell.Tekla.Module("Model Sharing") is Features.Tekla.ModelSharing.ModelSharingModule ms)
        {
            ms.Log = AppendLog;
            // Window-owned themed dialogs (mirror VPN) instead of the module's default owner-less MessageBox.
            ms.ConfirmHandler = msg =>
                ThemedDialogs.Show(this, msg, "Model Sharing", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            ms.ShowMessage = (msg, isError) =>
                ThemedDialogs.Show(this, msg, "Model Sharing", MessageBoxButton.OK,
                    isError ? MessageBoxImage.Warning : MessageBoxImage.Information);
            // Persistence stays in the shell: the module hands back the applied values to save the ModelSharing* keys.
            ms.OnProvisioned = info =>
            {
                _settings.ModelSharingTeklaBin = info.TeklaBin;
                _settings.ModelSharingServerHost = info.ServerHost;
                _settings.ModelSharingServerPort = info.ServerPort;
                _settings.ModelSharingIdentityEmail = info.IdentityEmail;
                _settings.ModelSharingLastAppliedUtc = info.AppliedUtc;
                _settingsService.Save(_settings);
            };
        }

        if (_shell.Tekla.Module("Патчинг") is Features.Tekla.Patching.PatchingModule patch)
        {
            patch.ConfirmHandler = msg =>
                ThemedDialogs.Show(this, msg, "Патчинг Tekla", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        }

        if (_shell.Structura.Module("Structura") is Features.Structura.StructuraModule st)
        {
            StructuraHost.Content = st.View;
            st.Log = AppendLog;
            st.Decrypt = SettingsService.DecryptToken;
        }

        if (_shell.Vpn.Module("Общая папка (VPN)") is Features.Vpn.VpnModule vpn)
        {
            VpnHost.Content = vpn.View;
            vpn.Log = AppendLog;
            vpn.DialogHandler = (msg, button, image) => ThemedDialogs.Show(this, msg, "VPN", button, image);
            // The big SMB-mount helper + in-memory creds stay in the shell; the module only requests "open this UNC".
            vpn.OpenShareHandler = OpenVpnShareAsync;
        }

        // Атрибуты: placeholder domain (no shell glue yet — pure roadmap view).
        AttributesHost.Content = _shell.Attributes.Module("Атрибуты")?.View;

        if (_shell.Platform.Module("Platform") is Features.Platform.PlatformModule platform)
        {
            PlatformHost.Content = platform.View;
            platform.Log = AppendLog;
        }

        // Конвертер (C2b): подвкладки «Конвертация | История»; строки «Конвертер: …» — в общий журнал окна.
        ConverterTabs.ItemsSource = _shell.Converter.Modules;
        if (_shell.Converter.Module("Конвертация") is Features.Converter.ConverterModule converter)
        {
            converter.Log = AppendLog;
        }
    }

    private void SyncFeatureModules()
    {
        if (_shell.Tekla.Module("Патчинг")?.View.DataContext is Features.Tekla.Patching.PatchingViewModel patch)
        {
            if (!string.IsNullOrWhiteSpace(_settings.IfcPatchingTeklaBin)) patch.TeklaBin = _settings.IfcPatchingTeklaBin;
            if (!string.IsNullOrWhiteSpace(_settings.IfcPatchingStagingDir)) patch.StagingDir = _settings.IfcPatchingStagingDir;
        }
        if (_shell.Tekla.Module("Model Sharing") is Features.Tekla.ModelSharing.ModelSharingModule ms)
        {
            ms.Initialize(
                new Features.Tekla.ModelSharing.ModelSharingIdentity(
                    _settings.DeviceId, _settings.IssuedTo, !string.IsNullOrWhiteSpace(_settings.DeviceId)),
                _settings.ModelSharingServerHost,
                _settings.ModelSharingServerPort,
                _settings.ModelSharingTeklaBin,
                _settings.TeklaExtensionsLocalPath);
        }

        if (_shell.Structura.Module("Structura") is Features.Structura.StructuraModule st)
        {
            st.Initialize(
                _settings.StructuraSpeckleUrl, _settings.StructuraSpeckleLogin, _settings.StructuraSpecklePasswordCipherBase64,
                _settings.StructuraNextcloudUrl, _settings.StructuraNextcloudLogin, _settings.StructuraNextcloudPasswordCipherBase64);
        }

        if (_shell.Vpn.Module("Общая папка (VPN)") is Features.Vpn.VpnModule vpn)
        {
            vpn.PushContext(new Features.Vpn.VpnContext(
                _settings.VpnEnabled, _settings.VpnSmbUnc, _settings.VpnServerIp,
                _lastVpnConfig, _lastSmbLogin, _lastSmbPassword));
        }
    }

    // Shell-owned SMB-over-VPN mount, invoked by the VPN module's OpenShareHandler. Ported verbatim from the old
    // VpnOpenFolder_Click: prefer the in-memory SMB creds, fall back to a plain explorer open, warn on failure.
    private async void OpenVpnShareAsync(string unc)
    {
        if (string.IsNullOrWhiteSpace(unc))
        {
            return;
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(_lastSmbLogin) && !string.IsNullOrWhiteSpace(_lastSmbPassword))
            {
                await ConnectSmbInternalAsync(_lastSmbLogin, _lastSmbPassword, unc, openExplorer: true);
                AppendLog("VPN: общая папка подключена (" + unc + ").");
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = unc, UseShellExecute = true });
                AppendLog("VPN: открыта папка без явного логина; при запросе введите SMB-логин или переподключитесь по токену.");
            }
        }
        catch (Exception ex)
        {
            AppendLog("VPN open folder error: " + ex.Message);
            try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = unc, UseShellExecute = true }); } catch { }
            ThemedDialogs.Show(this,
                "Не удалось автоматически подключить общую папку через VPN: " + ex.Message +
                "\n\nЕсли откроется окно проводника с запросом — введите SMB-логин и пароль из вкладки «Коннектор».",
                "VPN — общая папка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = await _runtimeServices.Host.PrepareLocalAsync();
            _converterWorkspace.ViewModel.RestoreSavedJobs(saved.Jobs);
            await _runtimeServices.Host.RecoverLocalAsync();
        }
        catch (Exception ex)
        {
            AppendLog("Локальная очередь не восстановлена: " + ex.GetType().Name + ". Исходные данные сохранены; требуется проверка журнала.");
        }
        await GraphiteWebHost.PublishSnapshotAsync(_graphite.CreateSnapshot());
        await GraphiteWebHost.InitializeAsync(_webViewUserDataDirectory);
        AppendLog("При закрытии окно сворачивается в трей. Для полного выхода: иконка в трее -> Закрыть.");
        if (_runLoadedExternalActions)
        {
            if (_teklaStandardService.CheckGitAvailability(out var gitPath, out var gitDetails))
            {
                AppendLog("Стандарт Tekla: git доступен (" + gitPath + ") " + gitDetails);
            }
            else
            {
                AppendLog("Стандарт Tekla: git недоступен (" + gitPath + ") " + gitDetails);
            }
            // Establish the device session and ensure its VPN before any automatic
            // server-bound update/sync work. On an already provisioned PC the
            // automatic tunnel service is up before Connector starts; on the first
            // run bootstrap supplies the config and Connector asks for UAC once.
            await TryAutoConnectAsync();
            await CheckUpdatesAsync(showDialogs: false);
            if (_standard is not null && !_runtimeServices.CommonAccess.IsSelected)
            {
                await _standard.RunTeklaSyncAsync(
                    showDialogs: false,
                    forceRefresh: false,
                    autoApplyIfPossible: true);
            }
            _backgroundAgent.Start(_isRunning);
        }
        await GraphiteWebHost.PublishSnapshotAsync(_graphite.CreateSnapshot());
        _loadedInitializationCompleted?.Invoke();
    }

    private async Task CheckAndOfferUpdatesAsync()
    {
        await CheckUpdatesAsync(showDialogs: false);
        await OfferUpdateInstallIfAvailableAsync();
    }

    private async Task OfferUpdateInstallIfAvailableAsync()
    {
        if (_updateOfferShown)
        {
            return;
        }

        if (_pendingUpdate is null && _pendingPackageUpdate is null)
        {
            return;
        }

        _updateOfferShown = true;

        var result = ThemedDialogs.Show(this,
            "Доступна новая версия Structura Connector. Установить обновление сейчас?",
            "Обновление доступно",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await InstallPendingUpdateAsync(confirmBeforeRun: false);
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка автообновления: " + ex.Message);
            _connector?.SetUpdateState("Обновление: ошибка установки");
            _updateOfferShown = false;
        }
    }

    private void ShowUpdateAvailableToast(UpdateManifest manifest)
    {
        var version = manifest.Version?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        if (string.Equals(_lastUpdateToastVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            if (_updateToastWindow is not null && _updateToastWindow.IsVisible)
            {
                _updateToastWindow.BringToFront();
            }
            return;
        }

        if (_updateToastWindow is not null && _updateToastWindow.IsVisible)
        {
            _updateToastWindow.Close();
            _updateToastWindow = null;
        }

        _lastUpdateToastVersion = version;
        var toast = new UpdateToastWindow(
            "Structura Connector",
            "Доступна новая версия: " + version + ".",
            async () => await InstallPendingUpdateAsync(confirmBeforeRun: false));
        toast.Closed += (_, _) =>
        {
            if (ReferenceEquals(_updateToastWindow, toast))
            {
                _updateToastWindow = null;
            }
        };
        _updateToastWindow = toast;
        toast.Show();
    }

    private void ShowPackageUpdateAvailableToast(PackageUpdateCandidate candidate)
    {
        if (string.Equals(_lastUpdateToastVersion, candidate.Version, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastUpdateToastVersion = candidate.Version;
        var toast = new UpdateToastWindow(
            "Structura Connector",
            "Доступна новая версия: " + candidate.Version + ".",
            async () => await InstallPendingUpdateAsync(confirmBeforeRun: false));
        toast.Closed += (_, _) =>
        {
            if (ReferenceEquals(_updateToastWindow, toast)) _updateToastWindow = null;
        };
        _updateToastWindow = toast;
        toast.Show();
    }

    private async Task TryAutoConnectAsync()
    {
        try
        {
            if (_runtimeServices.CommonAccess.IsSelected)
            {
                await _runtimeServices.ConnectCommonAsync(null, true, CancellationToken.None);
                return;
            }
            var token = SettingsService.DecryptToken(_settings.TokenCipherBase64).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                AppendLog("Сохраненного токена нет. Введите токен вручную.");
                return;
            }

            AppendLog("Найден сохраненный токен. Запускаю автоподключение...");
            await ConnectByTokenInternalAsync(token, showSuccessDialog: false);
        }
        catch (TaskCanceledException)
        {
            AppendLog("Автоподключение не выполнено: сервер ответил слишком медленно. Повторите подключение через кнопку.");
            _serverConnectionFailed = true;
            UpdateHeaderStatusUi();
        }
        catch (Exception ex)
        {
            AppendLog("Автоподключение не выполнено: " + ex.Message);
            _serverConnectionFailed = true;
            UpdateHeaderStatusUi();
        }
    }

    private Forms.NotifyIcon CreateTrayIcon(bool visible)
    {
        var menu = new Forms.ContextMenuStrip();

        var openItem = new Forms.ToolStripMenuItem("Открыть Structura Connector");
        openItem.Click += (_, _) => ShowFromTray();

        var closeItem = new Forms.ToolStripMenuItem("Закрыть");
        closeItem.Click += (_, _) => ExitFromTray();

        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(closeItem);

        var icon = TryGetTrayIcon();
        var tray = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Structura Connector",
            Visible = visible,
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => ShowFromTray();
        return tray;
    }

    private static System.Drawing.Icon TryGetTrayIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // Ignore icon extraction errors and use fallback icon.
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private Task RunHeartbeatTickAsync(CancellationToken cancellationToken) =>
        InvokeOnDispatcherAsync(() => SendHeartbeatSafeAsync(cancellationToken), cancellationToken);

    private Task RunUpdateTickAsync(CancellationToken cancellationToken) =>
        InvokeOnDispatcherAsync(
            () => CheckUpdatesAsync(showDialogs: false, cancellationToken: cancellationToken),
            cancellationToken);

    private Task RunTeklaSyncTickAsync(CancellationToken cancellationToken) =>
        InvokeOnDispatcherAsync(() =>
        {
            if (_standard is null || _runtimeServices.CommonAccess.IsSelected) return Task.CompletedTask;
            return _standard.RunTeklaSyncAsync(
                showDialogs: false,
                forceRefresh: false,
                autoApplyIfPossible: true);
        }, cancellationToken);

    private Task InvokeOnDispatcherAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        if (Dispatcher.CheckAccess()) return action();
        return Dispatcher
            .InvokeAsync(action, DispatcherPriority.Background, cancellationToken)
            .Task
            .Unwrap();
    }

    private void ReportBackgroundAgentError(DesktopBackgroundAction action, Exception exception)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() =>
            AppendLog($"Фоновая задача {action} завершилась с ошибкой: {exception.Message}"));
    }

    private void UpdateActionButtonUi()
    {
        if (_pendingUpdate is null && _pendingPackageUpdate is null)
        {
            _connector?.SetUpdateAction("Проверить обновление коннектора", isPrimaryStyle: false);
            return;
        }

        _connector?.SetUpdateAction("Скачать и установить обновление", isPrimaryStyle: true);
    }

    private void UpdateHeaderStatusUi()
    {
        var hasToken = !string.IsNullOrWhiteSpace(SettingsService.DecryptToken(_settings.TokenCipherBase64));
        if (!hasToken)
        {
            HeaderServerStatusTextBlock.Text = "Сервер: подключение не выполнено";
            HeaderServerStatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkGray;
        }
        else if (_isRunning && !string.IsNullOrWhiteSpace(_activeSessionId) && !_serverConnectionFailed)
        {
            HeaderServerStatusTextBlock.Text = "Сервер: подключено";
            HeaderServerStatusTextBlock.Foreground = System.Windows.Media.Brushes.MediumSpringGreen;
        }
        else if (_serverConnectionFailed)
        {
            HeaderServerStatusTextBlock.Text = "Сервер: подключение не выполнено";
            HeaderServerStatusTextBlock.Foreground = System.Windows.Media.Brushes.Orange;
        }
        else
        {
            HeaderServerStatusTextBlock.Text = "Сервер: проверка подключения...";
            HeaderServerStatusTextBlock.Foreground = System.Windows.Media.Brushes.Gainsboro;
        }

        // The Tekla overall status (HeaderFirmStatusTextBlock) now arrives from the Стандарт module via
        // IShellHost.OnTeklaStatusChanged; UpdateHeaderStatusUi only owns the server-status line.
    }

    private void ShowTeklaPendingBalloon(string revision)
    {
        if (_teklaBalloonShown)
        {
            return;
        }

        _teklaBalloonShown = true;
        _trayIcon.ShowBalloonTip(
            3000,
            "Стандарт Tekla",
            "Найдена ревизия " + revision + ". Закройте Tekla, и Connector применит обновление автоматически на следующей проверке.",
            Forms.ToolTipIcon.Info);
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(2500, "Structura Connector", "Приложение работает в трее. ПКМ по иконке -> Закрыть.", Forms.ToolTipIcon.Info);
        }
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private bool _exitInProgress;

    private async void ExitFromTray()
    {
        if (_exitInProgress)
        {
            return;
        }

        _exitInProgress = true;
        _allowClose = true;
        await DisposeWindowResourcesAsync();
        Close();
    }

    /// <summary>Disposes and closes an injected fixture window without using the production tray exit path.</summary>
    public async Task CloseOwnedCompositionAsync()
    {
        if (!_allowOwnedFixtureClose || _trayIcon.Visible)
        {
            throw new InvalidOperationException("Owned fixture close requires an injected window with its tray icon disabled.");
        }

        if (_exitInProgress)
        {
            if (_disposeResourcesTask is not null) await _disposeResourcesTask;
            return;
        }

        _exitInProgress = true;
        _allowClose = true;
        await DisposeWindowResourcesAsync();
        Close();
    }

    private Task DisposeWindowResourcesAsync() =>
        _disposeResourcesTask ??= DisposeWindowResourcesCoreAsync();

    private async Task DisposeWindowResourcesCoreAsync()
    {
        _isRunning = false;
        _trayIcon.Visible = false;
        DesktopBackgroundStopResult backgroundStop;
        try
        {
            backgroundStop = await _backgroundAgent.StopAsync(BackgroundShutdownDrainTimeout);
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка остановки фоновых задач: " + ex.Message);
            return;
        }

        if (!backgroundStop.IsDrained)
        {
            AppendLog("Фоновая операция ещё завершается; освобождение её ресурсов отложено до безопасной границы.");
            _deferredResourcesDisposeTask = DisposeWindowResourcesWhenBackgroundStopsAsync(backgroundStop.Completion);
            return;
        }

        try { await _backgroundAgent.DisposeAsync(); }
        catch (Exception ex) { AppendLog("Ошибка освобождения фоновых задач: " + ex.Message); }
        await DisposeWindowResourcesAfterBackgroundStopAsync();
    }

    private async Task DisposeWindowResourcesWhenBackgroundStopsAsync(Task backgroundCompletion)
    {
        try
        {
            await backgroundCompletion;
            await _backgroundAgent.DisposeAsync();
            await DisposeWindowResourcesAfterBackgroundStopAsync();
        }
        catch (Exception ex)
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                AppendLog("Ошибка отложенного освобождения ресурсов: " + ex.Message);
        }
    }

    private async Task DisposeWindowResourcesAfterBackgroundStopAsync()
    {
        _graphite.SnapshotChanged -= OnGraphiteSnapshot;
        try { await _graphite.DisposeAsync(); }
        catch (Exception ex) { AppendLog("Ошибка остановки интерфейса: " + ex.Message); }
        try { await GraphiteWebHost.DisposeAsync(); }
        catch (Exception ex) { AppendLog("Ошибка остановки WebView: " + ex.Message); }
        try { (_shell.Converter.Module("Конвертация") as IDisposable)?.Dispose(); }
        catch (Exception ex) { AppendLog("Ошибка остановки конвертера: " + ex.Message); }
        try { _converterWorkspace.Dispose(); }
        catch (Exception ex) { AppendLog("Ошибка остановки очереди конвертера: " + ex.Message); }
        try { _platformTools.Dispose(); }
        catch (Exception ex) { AppendLog("Ошибка остановки инструментов Platform: " + ex.Message); }
        if (_shell.Platform.Module("Platform") is IAsyncDisposable platform)
        {
            try
            {
                await platform.DisposeAsync();
            }
            catch (Exception ex)
            {
                AppendLog("Ошибка остановки Platform: " + ex.Message);
            }
        }
        try { await _runtimeServices.DisposeAsync(); }
        catch (Exception ex) { AppendLog("Ошибка остановки заданий: " + ex.Message); }
        _trayIcon.Dispose();
    }

    private string? GetUpdateRestartBlockReason()
    {
        if (_platformTools.ViewModel.IsBusy)
            return "Дождитесь завершения подготовки инструментов перед обновлением.";
        if (_runtimeServices.Host.DrainSnapshot.ActiveOperations > 0)
            return "Дождитесь завершения принятых заданий перед обновлением.";
        if (_teklaCheckInProgress)
        {
            return "Дождитесь завершения синхронизации Tekla перед обновлением.";
        }

        if (_shell.Converter.Module("Конвертация") is Features.Converter.ConverterModule converter &&
            converter.ViewModel.IsRunning)
        {
            return "Дождитесь завершения конвертации модели перед обновлением.";
        }

        return (_shell.Platform.Module("Platform") as Features.Platform.PlatformModule)?.GetRestartBlockReason();
    }

    private void LoadSettingsToUi()
    {
        _settings = _settingsService.Load();
        var shouldPersist = false;

        if (!string.IsNullOrWhiteSpace(_settings.SmbLogin))
        {
            _settings.SmbLogin = string.Empty;
            shouldPersist = true;
        }

        if (!string.IsNullOrWhiteSpace(_settings.SmbPasswordCipherBase64))
        {
            _settings.SmbPasswordCipherBase64 = string.Empty;
            shouldPersist = true;
        }

        _settings.ServerUrl = FixedServerUrl;
        _settings.UpdateManifestUrl = FixedUpdateManifestUrl;
        if (_settings.HeartbeatSeconds < 10)
        {
            _settings.HeartbeatSeconds = FixedHeartbeatSeconds;
        }
        if (string.IsNullOrWhiteSpace(_settings.SmbSharePath))
        {
            _settings.SmbSharePath = DefaultSmbSharePath;
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.TeklaStandardManifestUrl))
        {
            _settings.TeklaStandardManifestUrl = FixedTeklaStandardManifestUrl;
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.TeklaStandardLocalPath))
        {
            _settings.TeklaStandardLocalPath = DefaultTeklaStandardLocalPath;
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.TeklaExtensionsManifestUrl))
        {
            _settings.TeklaExtensionsManifestUrl = FixedTeklaExtensionsManifestUrl;
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.TeklaExtensionsLocalPath))
        {
            _settings.TeklaExtensionsLocalPath = DefaultTeklaExtensionsLocalPath;
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.TeklaPublishSourcePath) ||
            string.Equals(_settings.TeklaPublishSourcePath, @"\\62.113.36.107\BIM_Models\Tekla\XS_FIRM", StringComparison.OrdinalIgnoreCase))
        {
            _settings.TeklaPublishSourcePath = DefaultTeklaPublishSourcePath;
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.TeklaExtensionsPublishSourcePath) ||
            string.Equals(_settings.TeklaExtensionsPublishSourcePath, @"\\62.113.36.107\BIM_Models\Tekla\Extension", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_settings.TeklaExtensionsPublishSourcePath, @"\\62.113.36.107\BIM_Models\Tekla\Extensions", StringComparison.OrdinalIgnoreCase))
        {
            _settings.TeklaExtensionsPublishSourcePath = DefaultTeklaExtensionsPublishSourcePath;
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.TeklaLibrariesManifestUrl))
        {
            _settings.TeklaLibrariesManifestUrl = FixedTeklaLibrariesManifestUrl;
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.TeklaLibrariesLocalPath))
        {
            _settings.TeklaLibrariesLocalPath = DefaultTeklaLibrariesLocalPath;
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.TeklaLibrariesPublishSourcePath))
        {
            _settings.TeklaLibrariesPublishSourcePath = DefaultTeklaLibrariesPublishSourcePath;
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.StructuraSpeckleUrl))
        {
            _settings.StructuraSpeckleUrl = "https://speckle.structura-most.ru";
            shouldPersist = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.StructuraNextcloudUrl))
        {
            _settings.StructuraNextcloudUrl = "https://cloud.structura-most.ru";
            shouldPersist = true;
        }

        // The moved (mostly Collapsed) Коннектор controls + the token PasswordBox now live in ConnectorView; push the
        // live settings into them through the module (replaces the ServerUrl/UpdateManifestUrl/DeviceId/SmbLogin/
        // SmbSharePath/Interval/AutoStart/Token/SmbPassword writes — same values, sourced from the live AppSettings).
        // The Tekla local-path / publish-source TextBoxes live in the Стандарт module's StandardView; the module
        // populates them from _settings in its RefreshUi() (called from the ctor and after each token-connect).
        // restore the VPN config (decrypt) so "Включить VPN" works after a restart without re-connecting
        if (!string.IsNullOrWhiteSpace(_settings.VpnConfigCipherBase64))
        {
            _lastVpnConfig = SettingsService.DecryptToken(_settings.VpnConfigCipherBase64);
        }

        var token = SettingsService.DecryptToken(_settings.TokenCipherBase64);
        _connector?.LoadFromSettings(token);
        SyncFeatureModules();

        _backgroundAgent.SetHeartbeatInterval(TimeSpan.FromSeconds(_settings.HeartbeatSeconds));

        if (shouldPersist)
        {
            _settingsService.Save(_settings);
        }

        AppendLog($"Настройки загружены: {_settingsService.SettingsPath}");
    }

    private AppSettings ReadSettingsFromUi()
    {
        // The token PasswordBox moved into ConnectorView. ApplyAndPersist captures the typed token into
        // _settings.TokenCipherBase64 (via _connector.FlushEditsToSettings) BEFORE calling this, so source the token
        // from _settings here (decrypt+trim) — the empty-token guard still fires exactly as with the old PasswordBox read.
        var token = SettingsService.DecryptToken(_settings.TokenCipherBase64).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Токен не может быть пустым.");
        }

        var deviceId = string.IsNullOrWhiteSpace(_settings.DeviceId)
            ? "pc-" + Environment.MachineName.ToLowerInvariant()
            : _settings.DeviceId;

        var sec = _settings.HeartbeatSeconds >= 10 ? _settings.HeartbeatSeconds : FixedHeartbeatSeconds;
        var smbSharePath = string.IsNullOrWhiteSpace(_settings.SmbSharePath) ? DefaultSmbSharePath : _settings.SmbSharePath;
        var smbLogin = _settings.SmbLogin;
        var smbPassword = SettingsService.DecryptToken(_settings.SmbPasswordCipherBase64);
        var teklaManifestUrl = string.IsNullOrWhiteSpace(_settings.TeklaStandardManifestUrl)
            ? FixedTeklaStandardManifestUrl
            : _settings.TeklaStandardManifestUrl;
        // The Tekla local-path / publish-source TextBoxes moved into the Стандарт module's StandardView. The module
        // keeps _settings in sync with those edits (ApplyAndPersistTeklaPathsOnly + SaveSettings) before any save, so
        // the shell sources these values from _settings (same default fallbacks as the original TextBox-empty branch).
        var teklaLocalPath = string.IsNullOrWhiteSpace(_settings.TeklaStandardLocalPath)
            ? DefaultTeklaStandardLocalPath
            : _settings.TeklaStandardLocalPath;
        var teklaExtensionsManifestUrl = string.IsNullOrWhiteSpace(_settings.TeklaExtensionsManifestUrl)
            ? FixedTeklaExtensionsManifestUrl
            : _settings.TeklaExtensionsManifestUrl;
        var teklaExtensionsLocalPath = string.IsNullOrWhiteSpace(_settings.TeklaExtensionsLocalPath)
            ? DefaultTeklaExtensionsLocalPath
            : _settings.TeklaExtensionsLocalPath;
        var teklaLibrariesManifestUrl = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesManifestUrl)
            ? FixedTeklaLibrariesManifestUrl
            : _settings.TeklaLibrariesManifestUrl;
        var teklaLibrariesLocalPath = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesLocalPath)
            ? DefaultTeklaLibrariesLocalPath
            : _settings.TeklaLibrariesLocalPath;
        var teklaFirmPublishSourcePath = string.IsNullOrWhiteSpace(_settings.TeklaPublishSourcePath)
            ? DefaultTeklaPublishSourcePath
            : _settings.TeklaPublishSourcePath;
        var teklaExtensionsPublishSourcePath = string.IsNullOrWhiteSpace(_settings.TeklaExtensionsPublishSourcePath)
            ? DefaultTeklaExtensionsPublishSourcePath
            : _settings.TeklaExtensionsPublishSourcePath;
        var teklaLibrariesPublishSourcePath = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesPublishSourcePath)
            ? DefaultTeklaLibrariesPublishSourcePath
            : _settings.TeklaLibrariesPublishSourcePath;

        return new AppSettings
        {
            ServerUrl = FixedServerUrl,
            UpdateManifestUrl = string.IsNullOrWhiteSpace(_settings.UpdateManifestUrl)
                ? FixedUpdateManifestUrl
                : _settings.UpdateManifestUrl,
            DeviceId = deviceId,
            TokenCipherBase64 = SettingsService.EncryptToken(token),
            SmbLogin = smbLogin,
            SmbPasswordCipherBase64 = string.IsNullOrWhiteSpace(smbPassword)
                ? string.Empty
                : SettingsService.EncryptToken(smbPassword),
            SmbSharePath = smbSharePath,
            HeartbeatSeconds = sec,
            AutoStart = _settings.AutoStart,
            TeklaStandardManifestUrl = teklaManifestUrl,
            TeklaStandardLocalPath = teklaLocalPath,
            TeklaStandardInstalledVersion = _settings.TeklaStandardInstalledVersion,
            TeklaStandardTargetVersion = _settings.TeklaStandardTargetVersion,
            TeklaStandardInstalledRevision = _settings.TeklaStandardInstalledRevision,
            TeklaStandardTargetRevision = _settings.TeklaStandardTargetRevision,
            TeklaStandardLastCheckUtc = _settings.TeklaStandardLastCheckUtc,
            TeklaStandardLastSuccessUtc = _settings.TeklaStandardLastSuccessUtc,
            TeklaStandardPendingAfterClose = _settings.TeklaStandardPendingAfterClose,
            TeklaStandardLastError = _settings.TeklaStandardLastError,
            TeklaStandardLastTechnicalError = _settings.TeklaStandardLastTechnicalError,
            TeklaStandardRepoUrl = _settings.TeklaStandardRepoUrl,
            TeklaStandardRepoRef = _settings.TeklaStandardRepoRef,
            TeklaStandardRepoSubdir = _settings.TeklaStandardRepoSubdir,
            TeklaPublishSourcePath = teklaFirmPublishSourcePath,
            TeklaExtensionsManifestUrl = teklaExtensionsManifestUrl,
            TeklaExtensionsLocalPath = teklaExtensionsLocalPath,
            TeklaExtensionsInstalledVersion = _settings.TeklaExtensionsInstalledVersion,
            TeklaExtensionsTargetVersion = _settings.TeklaExtensionsTargetVersion,
            TeklaExtensionsInstalledRevision = _settings.TeklaExtensionsInstalledRevision,
            TeklaExtensionsTargetRevision = _settings.TeklaExtensionsTargetRevision,
            TeklaExtensionsLastCheckUtc = _settings.TeklaExtensionsLastCheckUtc,
            TeklaExtensionsLastSuccessUtc = _settings.TeklaExtensionsLastSuccessUtc,
            TeklaExtensionsPendingAfterClose = _settings.TeklaExtensionsPendingAfterClose,
            TeklaExtensionsLastError = _settings.TeklaExtensionsLastError,
            TeklaExtensionsLastTechnicalError = _settings.TeklaExtensionsLastTechnicalError,
            TeklaExtensionsRepoUrl = _settings.TeklaExtensionsRepoUrl,
            TeklaExtensionsRepoRef = _settings.TeklaExtensionsRepoRef,
            TeklaExtensionsRepoSubdir = _settings.TeklaExtensionsRepoSubdir,
            TeklaExtensionsPublishSourcePath = teklaExtensionsPublishSourcePath,
            TeklaLibrariesManifestUrl = teklaLibrariesManifestUrl,
            TeklaLibrariesLocalPath = teklaLibrariesLocalPath,
            TeklaLibrariesInstalledVersion = _settings.TeklaLibrariesInstalledVersion,
            TeklaLibrariesTargetVersion = _settings.TeklaLibrariesTargetVersion,
            TeklaLibrariesInstalledRevision = _settings.TeklaLibrariesInstalledRevision,
            TeklaLibrariesTargetRevision = _settings.TeklaLibrariesTargetRevision,
            TeklaLibrariesLastCheckUtc = _settings.TeklaLibrariesLastCheckUtc,
            TeklaLibrariesLastSuccessUtc = _settings.TeklaLibrariesLastSuccessUtc,
            TeklaLibrariesPendingAfterClose = _settings.TeklaLibrariesPendingAfterClose,
            TeklaLibrariesLastError = _settings.TeklaLibrariesLastError,
            TeklaLibrariesLastTechnicalError = _settings.TeklaLibrariesLastTechnicalError,
            TeklaLibrariesRepoUrl = _settings.TeklaLibrariesRepoUrl,
            TeklaLibrariesRepoRef = _settings.TeklaLibrariesRepoRef,
            TeklaLibrariesRepoSubdir = _settings.TeklaLibrariesRepoSubdir,
            TeklaLibrariesPublishSourcePath = teklaLibrariesPublishSourcePath,
            StructuraSpeckleUrl = _settings.StructuraSpeckleUrl,
            StructuraSpeckleLogin = _settings.StructuraSpeckleLogin,
            StructuraSpecklePasswordCipherBase64 = _settings.StructuraSpecklePasswordCipherBase64,
            StructuraNextcloudUrl = _settings.StructuraNextcloudUrl,
            StructuraNextcloudLogin = _settings.StructuraNextcloudLogin,
            StructuraNextcloudPasswordCipherBase64 = _settings.StructuraNextcloudPasswordCipherBase64,
            IsSystemAdmin = _settings.IsSystemAdmin,
            IsFirmAdmin = _settings.IsFirmAdmin,
            IssuedTo = _settings.IssuedTo,
            ModelSharingTeklaBin = _settings.ModelSharingTeklaBin,
            ModelSharingServerHost = string.IsNullOrWhiteSpace(_settings.ModelSharingServerHost) ? "62.113.36.107" : _settings.ModelSharingServerHost,
            ModelSharingServerPort = _settings.ModelSharingServerPort > 0 ? _settings.ModelSharingServerPort : 9990,
            ModelSharingIdentityEmail = _settings.ModelSharingIdentityEmail,
            ModelSharingLastAppliedUtc = _settings.ModelSharingLastAppliedUtc,
            IfcPatchingTeklaBin = _settings.IfcPatchingTeklaBin,
            IfcPatchingStagingDir = _settings.IfcPatchingStagingDir,
            ConverterOutputDirectory = _settings.ConverterOutputDirectory,
            VpnEnabled = _settings.VpnEnabled,
            VpnTunnelName = _settings.VpnTunnelName,
            VpnAddress = _settings.VpnAddress,
            VpnSmbUnc = _settings.VpnSmbUnc,
            VpnServerIp = _settings.VpnServerIp,
            VpnConfigReceivedUtc = _settings.VpnConfigReceivedUtc,
            VpnConfigCipherBase64 = _settings.VpnConfigCipherBase64,
            ExtensionData = _settings.ExtensionData
        };
    }

    private void ApplyAndPersist()
    {
        _standard?.FlushPathEdits();   // capture typed-but-unbrowsed Tekla path edits before reading settings
        // Capture the typed token from the moved Коннектор PasswordBox into _settings before ReadSettingsFromUi reads
        // it back (mirrors StandardModule.FlushPathEdits). ReadSettingsFromUi now sources the token from _settings.
        _settings.TokenCipherBase64 = SettingsService.EncryptToken(_connector?.FlushEditsToSettings() ?? string.Empty);
        _settings = ReadSettingsFromUi();
        _settingsService.Save(_settings);
        _autoStartService.SetEnabled(_settings.AutoStart);
        _backgroundAgent.SetHeartbeatInterval(TimeSpan.FromSeconds(_settings.HeartbeatSeconds));
        _standard?.RefreshUi();
        AppendLog("Настройки сохранены.");
    }

    private void UpdateRunStateUi()
    {
        string text;
        System.Windows.Media.Brush brush;
        if (_isRunning)
        {
            text = "Автоотправка heartbeat: включена";
            brush = System.Windows.Media.Brushes.MediumSpringGreen;
        }
        else
        {
            text = "Автоотправка heartbeat: выключена";
            brush = System.Windows.Media.Brushes.Orange;
        }

        // RunStateTextBlock + Start/StopButton moved into ConnectorView; push the computed text/brush/enabled state.
        _connector?.SetRunState(text, brush, !_isRunning, _isRunning);
        UpdateHeaderStatusUi();
    }

    private async Task SendHeartbeatSafeAsync(CancellationToken cancellationToken = default)
    {
        if (_runtimeServices.CommonAccess.IsSelected) return;
        try
        {
            var token = SettingsService.DecryptToken(_settings.TokenCipherBase64);
            var teklaRunning = _teklaStandardService.IsTeklaRunning();
            var teklaState = new TeklaHeartbeatState
            {
                InstalledVersion = _settings.TeklaStandardInstalledVersion,
                TargetVersion = _settings.TeklaStandardTargetVersion,
                InstalledRevision = _settings.TeklaStandardInstalledRevision,
                TargetRevision = _settings.TeklaStandardTargetRevision,
                PendingAfterClose =
                    _settings.TeklaStandardPendingAfterClose ||
                    _settings.TeklaExtensionsPendingAfterClose ||
                    _settings.TeklaLibrariesPendingAfterClose,
                TeklaRunning = teklaRunning,
                LastCheckUtc = _settings.TeklaStandardLastCheckUtc?.UtcDateTime.ToString("o") ?? string.Empty,
                LastSuccessUtc = _settings.TeklaStandardLastSuccessUtc?.UtcDateTime.ToString("o") ?? string.Empty,
                LastError = FirstNonEmpty(
                    _settings.TeklaStandardLastError,
                    _settings.TeklaExtensionsLastError,
                    _settings.TeklaLibrariesLastError)
            };

            await _heartbeatClient.SendHeartbeatAsync(
                _settings.ServerUrl,
                _settings.DeviceId,
                token,
                _activeSessionId,
                teklaState,
                cancellationToken);
            _serverConnectionFailed = false;
            AppendLog("Heartbeat отправлен успешно.");
            UpdateHeaderStatusUi();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("HTTP 409", StringComparison.OrdinalIgnoreCase))
            {
                _backgroundAgent.SetHeartbeatEnabled(false);
                _isRunning = false;
                _serverConnectionFailed = true;
                UpdateRunStateUi();
                AppendLog("Сессия отключена: этот токен активирован на другом устройстве.");
                return;
            }

            _serverConnectionFailed = true;
            AppendLog("Ошибка heartbeat: " + ex.Message);
            UpdateHeaderStatusUi();
        }
    }

    private void AppendLog(string text)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyAndPersist();
            _backgroundAgent.Start(heartbeatEnabled: true);
            _isRunning = true;
            UpdateRunStateUi();
            AppendLog("Фоновая отправка запущена.");
            await SendHeartbeatSafeAsync();
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка запуска: " + ex.Message);
            ThemedDialogs.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _backgroundAgent.SetHeartbeatEnabled(false);
        _isRunning = false;
        UpdateRunStateUi();
        AppendLog("Фоновая отправка остановлена.");
    }

    private async void SendNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyAndPersist();
            await SendHeartbeatSafeAsync();
        }
        catch (Exception ex)
        {
            ThemedDialogs.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // ServerUrlTextBox moved into ConnectorView (Collapsed); re-source from _settings (LoadFromSettings keeps it
            // mirrored to _settings.ServerUrl, which is always FixedServerUrl — same value the old Collapsed box held).
            var serverUrl = _settings.ServerUrl.Trim();
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException("Введите корректный URL сервера.");
            }

            await _heartbeatClient.CheckServerHealthAsync(serverUrl, CancellationToken.None);
            try
            {
                var ip = await _heartbeatClient.ResolvePublicIpAsync(CancellationToken.None);
                AppendLog("Подключение к серверу проверено. Внешний IP: " + ip);
                ThemedDialogs.Show(this, 
                    "Сервер доступен и отвечает /health.\nВнешний IP: " + ip,
                    "Проверка подключения",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ipEx)
            {
                AppendLog("Сервер доступен, но внешний IP определить не удалось: " + ipEx.Message);
                ThemedDialogs.Show(this, 
                    "Сервер доступен и отвечает /health.\n" +
                    "Но внешний IP определить не удалось, поэтому отправка heartbeat может не работать.\n\n" +
                    ipEx.Message,
                    "Проверка подключения",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка проверки подключения: " + ex.Message);
            ThemedDialogs.Show(this, ex.Message, "Ошибка проверки подключения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyAndPersist();
        }
        catch (Exception ex)
        {
            ThemedDialogs.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // (ConnectByToken_Click moved into ConnectorView — it calls the SACRED engine below via IConnectorHost.ConnectByTokenAsync.)

    private async Task ConnectByTokenInternalAsync(string token, bool showSuccessDialog)
    {
        if (_runtimeServices.CommonAccess.ShouldUse(token))
        {
            await _runtimeServices.ConnectCommonAsync(token, false, CancellationToken.None);
            return;
        }
        var serverUrl = FixedServerUrl;

        AppendLog("Запрошен bootstrap по токену...");
        var bootstrap = await _heartbeatClient.BootstrapAsync(serverUrl, token, CancellationToken.None);

        if (string.IsNullOrWhiteSpace(bootstrap.DeviceId))
        {
            throw new InvalidOperationException("Сервер вернул пустой device_id.");
        }

        var sharePath = bootstrap.SmbAccess.ShareUnc;
        if (string.IsNullOrWhiteSpace(sharePath))
        {
            sharePath = bootstrap.SmbAccess.SharePath;
        }

        if (string.IsNullOrWhiteSpace(bootstrap.SmbAccess.Login) ||
            string.IsNullOrWhiteSpace(bootstrap.SmbAccess.Password) ||
            string.IsNullOrWhiteSpace(sharePath))
        {
            throw new InvalidOperationException("Сервер не вернул полный набор SMB-данных для подключения.");
        }

        _settings = new AppSettings
        {
            ServerUrl = FixedServerUrl,
            UpdateManifestUrl = string.IsNullOrWhiteSpace(bootstrap.UpdateManifestUrl)
                ? FixedUpdateManifestUrl
                : bootstrap.UpdateManifestUrl,
            DeviceId = bootstrap.DeviceId,
            TokenCipherBase64 = SettingsService.EncryptToken(token),
            SmbLogin = string.Empty,
            SmbPasswordCipherBase64 = string.Empty,
            SmbSharePath = sharePath,
            HeartbeatSeconds = bootstrap.HeartbeatSeconds >= 10 ? bootstrap.HeartbeatSeconds : FixedHeartbeatSeconds,
            AutoStart = _settings.AutoStart,
            TeklaStandardManifestUrl = string.IsNullOrWhiteSpace(_settings.TeklaStandardManifestUrl)
                ? FixedTeklaStandardManifestUrl
                : _settings.TeklaStandardManifestUrl,
            TeklaStandardLocalPath = string.IsNullOrWhiteSpace(_settings.TeklaStandardLocalPath)
                ? DefaultTeklaStandardLocalPath
                : _settings.TeklaStandardLocalPath,
            TeklaStandardInstalledVersion = _settings.TeklaStandardInstalledVersion,
            TeklaStandardTargetVersion = _settings.TeklaStandardTargetVersion,
            TeklaStandardInstalledRevision = _settings.TeklaStandardInstalledRevision,
            TeklaStandardTargetRevision = _settings.TeklaStandardTargetRevision,
            TeklaStandardLastCheckUtc = _settings.TeklaStandardLastCheckUtc,
            TeklaStandardLastSuccessUtc = _settings.TeklaStandardLastSuccessUtc,
            TeklaStandardPendingAfterClose = _settings.TeklaStandardPendingAfterClose,
            TeklaStandardLastError = _settings.TeklaStandardLastError,
            TeklaStandardLastTechnicalError = _settings.TeklaStandardLastTechnicalError,
            TeklaStandardRepoUrl = _settings.TeklaStandardRepoUrl,
            TeklaStandardRepoRef = _settings.TeklaStandardRepoRef,
            TeklaStandardRepoSubdir = _settings.TeklaStandardRepoSubdir,
            TeklaPublishSourcePath = string.IsNullOrWhiteSpace(_settings.TeklaPublishSourcePath)
                ? DefaultTeklaPublishSourcePath
                : _settings.TeklaPublishSourcePath,
            TeklaExtensionsManifestUrl = string.IsNullOrWhiteSpace(_settings.TeklaExtensionsManifestUrl)
                ? FixedTeklaExtensionsManifestUrl
                : _settings.TeklaExtensionsManifestUrl,
            TeklaExtensionsLocalPath = string.IsNullOrWhiteSpace(_settings.TeklaExtensionsLocalPath)
                ? DefaultTeklaExtensionsLocalPath
                : _settings.TeklaExtensionsLocalPath,
            TeklaExtensionsInstalledVersion = _settings.TeklaExtensionsInstalledVersion,
            TeklaExtensionsTargetVersion = _settings.TeklaExtensionsTargetVersion,
            TeklaExtensionsInstalledRevision = _settings.TeklaExtensionsInstalledRevision,
            TeklaExtensionsTargetRevision = _settings.TeklaExtensionsTargetRevision,
            TeklaExtensionsLastCheckUtc = _settings.TeklaExtensionsLastCheckUtc,
            TeklaExtensionsLastSuccessUtc = _settings.TeklaExtensionsLastSuccessUtc,
            TeklaExtensionsPendingAfterClose = _settings.TeklaExtensionsPendingAfterClose,
            TeklaExtensionsLastError = _settings.TeklaExtensionsLastError,
            TeklaExtensionsLastTechnicalError = _settings.TeklaExtensionsLastTechnicalError,
            TeklaExtensionsRepoUrl = _settings.TeklaExtensionsRepoUrl,
            TeklaExtensionsRepoRef = _settings.TeklaExtensionsRepoRef,
            TeklaExtensionsRepoSubdir = _settings.TeklaExtensionsRepoSubdir,
            TeklaExtensionsPublishSourcePath = string.IsNullOrWhiteSpace(_settings.TeklaExtensionsPublishSourcePath)
                ? DefaultTeklaExtensionsPublishSourcePath
                : _settings.TeklaExtensionsPublishSourcePath,
            TeklaLibrariesManifestUrl = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesManifestUrl)
                ? FixedTeklaLibrariesManifestUrl
                : _settings.TeklaLibrariesManifestUrl,
            TeklaLibrariesLocalPath = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesLocalPath)
                ? DefaultTeklaLibrariesLocalPath
                : _settings.TeklaLibrariesLocalPath,
            TeklaLibrariesInstalledVersion = _settings.TeklaLibrariesInstalledVersion,
            TeklaLibrariesTargetVersion = _settings.TeklaLibrariesTargetVersion,
            TeklaLibrariesInstalledRevision = _settings.TeklaLibrariesInstalledRevision,
            TeklaLibrariesTargetRevision = _settings.TeklaLibrariesTargetRevision,
            TeklaLibrariesLastCheckUtc = _settings.TeklaLibrariesLastCheckUtc,
            TeklaLibrariesLastSuccessUtc = _settings.TeklaLibrariesLastSuccessUtc,
            TeklaLibrariesPendingAfterClose = _settings.TeklaLibrariesPendingAfterClose,
            TeklaLibrariesLastError = _settings.TeklaLibrariesLastError,
            TeklaLibrariesLastTechnicalError = _settings.TeklaLibrariesLastTechnicalError,
            TeklaLibrariesRepoUrl = _settings.TeklaLibrariesRepoUrl,
            TeklaLibrariesRepoRef = _settings.TeklaLibrariesRepoRef,
            TeklaLibrariesRepoSubdir = _settings.TeklaLibrariesRepoSubdir,
            TeklaLibrariesPublishSourcePath = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesPublishSourcePath)
                ? DefaultTeklaLibrariesPublishSourcePath
                : _settings.TeklaLibrariesPublishSourcePath,
            StructuraSpeckleUrl = string.IsNullOrWhiteSpace(bootstrap.WebAccess.Speckle.Url)
                ? (string.IsNullOrWhiteSpace(_settings.StructuraSpeckleUrl) ? "https://speckle.structura-most.ru" : _settings.StructuraSpeckleUrl)
                : bootstrap.WebAccess.Speckle.Url,
            StructuraSpeckleLogin = bootstrap.WebAccess.Speckle.Login,
            StructuraSpecklePasswordCipherBase64 = string.IsNullOrWhiteSpace(bootstrap.WebAccess.Speckle.Password)
                ? string.Empty
                : SettingsService.EncryptToken(bootstrap.WebAccess.Speckle.Password),
            StructuraNextcloudUrl = string.IsNullOrWhiteSpace(bootstrap.WebAccess.Nextcloud.Url)
                ? (string.IsNullOrWhiteSpace(_settings.StructuraNextcloudUrl) ? "https://cloud.structura-most.ru" : _settings.StructuraNextcloudUrl)
                : bootstrap.WebAccess.Nextcloud.Url,
            StructuraNextcloudLogin = bootstrap.WebAccess.Nextcloud.Login,
            StructuraNextcloudPasswordCipherBase64 = string.IsNullOrWhiteSpace(bootstrap.WebAccess.Nextcloud.Password)
                ? string.Empty
                : SettingsService.EncryptToken(bootstrap.WebAccess.Nextcloud.Password),
            IsSystemAdmin = bootstrap.IsSystemAdmin,
            IsFirmAdmin = bootstrap.IsFirmAdmin,
            IssuedTo = string.IsNullOrWhiteSpace(bootstrap.IssuedTo) ? _settings.IssuedTo : bootstrap.IssuedTo,
            ModelSharingTeklaBin = _settings.ModelSharingTeklaBin,
            ModelSharingServerHost = string.IsNullOrWhiteSpace(_settings.ModelSharingServerHost) ? "62.113.36.107" : _settings.ModelSharingServerHost,
            ModelSharingServerPort = _settings.ModelSharingServerPort > 0 ? _settings.ModelSharingServerPort : 9990,
            ModelSharingIdentityEmail = _settings.ModelSharingIdentityEmail,
            ModelSharingLastAppliedUtc = _settings.ModelSharingLastAppliedUtc,
            IfcPatchingTeklaBin = _settings.IfcPatchingTeklaBin,
            IfcPatchingStagingDir = _settings.IfcPatchingStagingDir,
            ConverterOutputDirectory = _settings.ConverterOutputDirectory,
            // Preserve the last known-good tunnel until bootstrap supplies a
            // replacement. A transient VPN provisioning error must not disable
            // the controls or strand a working automatic tunnel.
            VpnEnabled = _settings.VpnEnabled,
            VpnTunnelName = _settings.VpnTunnelName,
            VpnAddress = _settings.VpnAddress,
            VpnSmbUnc = _settings.VpnSmbUnc,
            VpnServerIp = _settings.VpnServerIp,
            VpnConfigReceivedUtc = _settings.VpnConfigReceivedUtc,
            VpnConfigCipherBase64 = _settings.VpnConfigCipherBase64,
            ExtensionData = _settings.ExtensionData
        };
        _settingsService.Save(_settings);
        _autoStartService.SetEnabled(_settings.AutoStart);
        _backgroundAgent.SetHeartbeatInterval(TimeSpan.FromSeconds(_settings.HeartbeatSeconds));
        _activeSessionId = bootstrap.SessionId;
        _serverConnectionFailed = false;

        // The six post-connect Коннектор controls moved into ConnectorView; push the same values across the seam
        // (DeviceId/Interval/UpdateManifestUrl/SmbLogin + SmbPassword cleared + SmbSharePath — exact original values).
        _connector?.ApplyConnectResult(_settings.DeviceId, _settings.HeartbeatSeconds, _settings.UpdateManifestUrl, bootstrap.SmbAccess.Login, _settings.SmbSharePath);
        // SMB creds (kept in memory) so we can mount the share over VPN with the right login.
        _lastSmbLogin = bootstrap.SmbAccess.Login;
        _lastSmbPassword = bootstrap.SmbAccess.Password;

        // VPN bundle from bootstrap (optional; gated by server). Config is persisted ENCRYPTED
        // (DPAPI) so "Включить VPN" works after a restart; kept in memory for immediate use.
        var bootstrapVpnAvailable =
            bootstrap.Vpn.Enabled &&
            !string.IsNullOrWhiteSpace(bootstrap.Vpn.Config);
        if (bootstrapVpnAvailable)
        {
            _settings.VpnEnabled = true;
            _lastVpnConfig = bootstrap.Vpn.Config;
            _settings.VpnTunnelName = bootstrap.Vpn.TunnelName;   // server-side tunnel name (informational)
            _settings.VpnAddress = bootstrap.Vpn.Address;
            _settings.VpnSmbUnc = bootstrap.Vpn.SmbUnc;
            _settings.VpnServerIp = bootstrap.Vpn.ServerVpnIp;
            _settings.VpnConfigReceivedUtc = DateTimeOffset.UtcNow;
            _settings.VpnConfigCipherBase64 = SettingsService.EncryptToken(bootstrap.Vpn.Config);
            _settingsService.Save(_settings);
            AppendLog("Получена конфигурация VPN для доступа к общей папке.");
        }
        else if (_settings.VpnEnabled && !string.IsNullOrWhiteSpace(_lastVpnConfig))
        {
            AppendLog(
                "Сервер временно не обновил VPN-конфигурацию; используется последняя сохранённая рабочая версия.");
        }

        _standard?.RefreshUi();
        SyncFeatureModules();
        AppendLog("Настройки сохранены.");

        var vpnReady = false;
        if (_settings.VpnEnabled &&
            _shell.Vpn.Module("Общая папка (VPN)") is Features.Vpn.VpnModule vpnModule)
        {
            AppendLog("VPN включён по умолчанию. Проверяю автоматический туннель...");
            var vpnResult = await vpnModule.EnsureEnabledAsync(
                showResultDialog: false,
                openShareOnSuccess: false);
            vpnReady = vpnResult.IsSuccess;
            if (vpnReady)
            {
                AppendLog("VPN готов. Дальнейшее подключение к серверу идёт через защищённый туннель.");
            }
            else
            {
                AppendLog(
                    "Автоматическое включение VPN не выполнено: " + vpnResult.Message +
                    " Connector продолжит работу и попробует прямое подключение.");
            }
        }

        var smbConnected = false;
        var smbConnectionRoute = string.Empty;
        var preferredSharePath = vpnReady ? ResolveVpnSmbUnc() : sharePath;
        if (string.IsNullOrWhiteSpace(preferredSharePath))
        {
            preferredSharePath = sharePath;
        }
        var preferredSmbHost = GetSmbHost(preferredSharePath);
        var preferredSmbReachable = await _tcpConnectivityProbe.CanConnectAsync(
            preferredSmbHost,
            445,
            TimeSpan.FromSeconds(4));

        if (preferredSmbReachable)
        {
            try
            {
                await ConnectSmbInternalAsync(
                    bootstrap.SmbAccess.Login,
                    bootstrap.SmbAccess.Password,
                    preferredSharePath,
                    openExplorer: true);
                smbConnected = true;
                smbConnectionRoute = vpnReady ? "через VPN" : "напрямую";
                if (vpnReady)
                {
                    AppendLog("Общая папка автоматически подключена через VPN.");
                }
            }
            catch (Exception ex) when (IsWindowsSmbConflict(ex))
            {
                AppendLog("SMB-подключение не переключено автоматически (конфликт 1219). Текущая сессия SMB оставлена без изменений.");
                AppendLog("Детали SMB конфликта: " + ex.Message);
            }
            catch (Exception ex)
            {
                AppendLog("SMB-подключение не выполнено: " + ex.Message);
            }
        }
        else
        {
            AppendLog(
                $"SMB недоступен: сервер {preferredSmbHost}:445 не отвечает " +
                (vpnReady ? "через VPN." : "из этой сети."));
        }

        _backgroundAgent.Start(heartbeatEnabled: true);
        _isRunning = true;
        UpdateRunStateUi();

        await SendHeartbeatSafeAsync();
        AppendLog("Автоподключение по токену выполнено успешно.");

        if (showSuccessDialog)
        {
            ThemedDialogs.Show(this, 
                smbConnected
                    ? $"Подключение выполнено. Общая SMB-папка подключена {smbConnectionRoute}, автоотправка heartbeat включена."
                    : "Подключение к серверу выполнено, heartbeat включён, но общая папка пока не подключена. " +
                      "Проверьте статус VPN на вкладке «Общая папка (VPN)» и повторите подключение.",
                "Structura Connector",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private async Task CheckUpdatesAsync(bool showDialogs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_updateCheckInProgress)
        {
            return;
        }

        _updateCheckInProgress = true;
        _connector?.SetUpdateActionEnabled(false);
        try
        {
            if (_packageUpdateService.IsManagedInstall)
            {
                await CheckPackageUpdatesAsync(showDialogs, cancellationToken);
                return;
            }

            if (_runtimeServices.CommonAccess.IsSelected) return;

            var manifestUrl = string.IsNullOrWhiteSpace(_settings.UpdateManifestUrl)
                ? FixedUpdateManifestUrl
                : _settings.UpdateManifestUrl.Trim();
            if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException("Введите корректный адрес обновлений.");
            }

            _settings.UpdateManifestUrl = manifestUrl;
            _connector?.SetUpdateManifestUrl(manifestUrl);
            _settingsService.Save(_settings);

            var manifest = await _updateService.TryGetUpdateAsync(manifestUrl, cancellationToken);
            if (manifest is null)
            {
                _pendingUpdate = null;
                _lastUpdateToastVersion = string.Empty;
                _connector?.SetUpdateState("Обновление: не удалось получить данные");
                UpdateActionButtonUi();
                if (showDialogs)
                {
                    ThemedDialogs.Show(this, "Не удалось проверить обновления.", "Обновления", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            if (_updateService.IsUpdateAvailable(manifest))
            {
                _pendingUpdate = manifest;
                _connector?.SetUpdateState($"Доступно обновление: {manifest.Version}");
                AppendLog("Найдено обновление: " + manifest.Version);
                ShowUpdateAvailableToast(manifest);
                if (!showDialogs)
                {
                    UpdateActionButtonUi();
                }
                else
                {
                    UpdateActionButtonUi();
                }
                if (showDialogs)
                {
                    ThemedDialogs.Show(this,
                        "Доступна новая версия: " + manifest.Version +
                        (string.IsNullOrWhiteSpace(manifest.Notes) ? "" : "\n\n" + manifest.Notes),
                        "Обновления",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            else
            {
                _pendingUpdate = null;
                _lastUpdateToastVersion = string.Empty;
                _connector?.SetUpdateState($"Обновление: актуально ({_updateService.CurrentVersion})");
                UpdateActionButtonUi();
                if (showDialogs)
                {
                    ThemedDialogs.Show(this, "Установлена актуальная версия.", "Обновления", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _pendingUpdate = null;
            _connector?.SetUpdateState("Обновление: ошибка проверки");
            AppendLog("Ошибка проверки обновления: " + ex.Message);
            UpdateActionButtonUi();
            if (showDialogs)
            {
                ThemedDialogs.Show(this, ex.Message, "Ошибка обновлений", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _updateCheckInProgress = false;
            _connector?.SetUpdateActionEnabled(true);
        }
    }

    // (UpdateAction_Click + ShowReleaseNotes_Click moved into ConnectorView — they route through IConnectorHost
    // [HasPendingUpdate/CheckUpdatesAsync/InstallPendingUpdateAsync] and IConnectorHost.ReleaseNotes/OwnerWindow.)

    // Native support action: verification/download only. Applying remains exclusively in
    // InstallPendingUpdateAsync, including its existing drain/restart gates.
    private async Task DownloadPendingUpdateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _connector?.SetUpdateActionEnabled(false);
        try
        {
            if (_packageUpdateService.IsManagedInstall)
            {
                var candidate = _pendingPackageUpdate ?? throw new InvalidOperationException("Сначала проверьте наличие пакетного обновления.");
                _connector?.SetUpdateState("Обновление: загрузка пакета...");
                await _packageUpdateService.DownloadUpdatesAsync(candidate, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                AppendLog("Пакет обновления загружен: " + candidate.Version);
                _connector?.SetUpdateState("Обновление: пакет загружен");
                return;
            }

            var manifest = _pendingUpdate ?? throw new InvalidOperationException("Сначала проверьте наличие обновления.");
            _connector?.SetUpdateState("Обновление: загрузка установщика...");
            _downloadedInstallerPath = await _updateService.DownloadInstallerAsync(manifest, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            AppendLog("Установщик обновления проверен и загружен: " + _downloadedInstallerPath);
            _connector?.SetUpdateState("Обновление: установщик загружен");
        }
        catch (OperationCanceledException)
        {
            _connector?.SetUpdateState("Обновление: загрузка отменена");
            throw;
        }
        catch (Exception ex)
        {
            _connector?.SetUpdateState("Обновление: ошибка загрузки");
            AppendLog("Ошибка загрузки обновления: " + ex.Message);
            throw;
        }
        finally
        {
            _connector?.SetUpdateActionEnabled(_pendingUpdate is not null || _pendingPackageUpdate is not null);
        }
    }

    private async Task InstallPendingUpdateAsync(bool confirmBeforeRun)
    {
        try
        {
            if (_packageUpdateService.IsManagedInstall)
            {
                await InstallPendingPackageUpdateAsync(confirmBeforeRun);
                return;
            }

            if (_pendingUpdate is null)
            {
                await CheckUpdatesAsync(showDialogs: true);
                if (_pendingUpdate is null)
                {
                    return;
                }
            }

            _connector?.SetUpdateActionEnabled(false);
            _connector?.SetUpdateState("Обновление: загрузка установщика...");
            _downloadedInstallerPath = await _updateService.DownloadInstallerAsync(_pendingUpdate, CancellationToken.None);
            AppendLog("Скачан установщик обновления: " + _downloadedInstallerPath);

            var shouldRunInstaller = !confirmBeforeRun || ThemedDialogs.Show(this,
                "Установщик скачан. Закрыть приложение и запустить обновление сейчас?",
                "Обновление",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;

            if (shouldRunInstaller)
            {
                var restartBlockReason = GetUpdateRestartBlockReason();
                if (restartBlockReason is not null)
                {
                    _connector?.SetUpdateState("Обновление: ожидает завершения задачи");
                    ThemedDialogs.Show(this, restartBlockReason, "Обновление", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                UpdateService.RunInstaller(_downloadedInstallerPath);
                ExitFromTray();
            }
            else
            {
                _connector?.SetUpdateState("Обновление: установщик скачан");
                _connector?.SetUpdateActionEnabled(true);
            }
        }
        catch (Exception ex)
        {
            _connector?.SetUpdateActionEnabled(_pendingUpdate is not null);
            _connector?.SetUpdateState("Обновление: ошибка установки");
            AppendLog("Ошибка установки обновления: " + ex.Message);
            ThemedDialogs.Show(this, ex.Message, "Ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CheckPackageUpdatesAsync(bool showDialogs, CancellationToken cancellationToken = default)
    {
        _connector?.SetUpdateState("Обновление: проверка пакетной версии...");
        var candidate = await _packageUpdateService.CheckForUpdatesAsync(cancellationToken);
        _pendingPackageUpdate = candidate;
        _pendingUpdate = null;
        if (candidate is null)
        {
            _lastUpdateToastVersion = string.Empty;
            _connector?.SetUpdateState("Обновление: актуально");
            UpdateActionButtonUi();
            if (showDialogs) ThemedDialogs.Show(this, "Установлена актуальная версия.", "Обновления", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _connector?.SetUpdateState($"Доступно обновление: {candidate.Version}");
        AppendLog("Найдено пакетное обновление: " + candidate.Version);
        ShowPackageUpdateAvailableToast(candidate);
        UpdateActionButtonUi();
        if (showDialogs) ThemedDialogs.Show(this, "Доступна новая версия: " + candidate.Version, "Обновления", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task InstallPendingPackageUpdateAsync(bool confirmBeforeRun)
    {
        if (_pendingPackageUpdate is null)
        {
            await CheckPackageUpdatesAsync(showDialogs: true);
            if (_pendingPackageUpdate is null) return;
        }

        _connector?.SetUpdateActionEnabled(false);
        var drained = false;
        var handedOff = false;
        try
        {
            var candidate = _pendingPackageUpdate;
            _connector?.SetUpdateState("Обновление: загрузка пакета...");
            await _packageUpdateService.DownloadUpdatesAsync(candidate, CancellationToken.None);
            AppendLog("Пакет обновления загружен: " + candidate.Version);

            var shouldApply = !confirmBeforeRun || ThemedDialogs.Show(this,
                "Пакет обновления загружен. Перезапустить Connector и применить его сейчас?",
                "Обновление", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!shouldApply)
            {
                _connector?.SetUpdateState("Обновление: пакет загружен");
                return;
            }

            // Legacy sync/batch discovery remains guarded until it joins Agent admission.
            if (_teklaCheckInProgress ||
                (_shell.Converter.Module("Конвертация") is Features.Converter.ConverterModule batch && batch.ViewModel.IsRunning) ||
                _platformTools.ViewModel.IsBusy)
            {
                _connector?.SetUpdateState("Обновление: ожидает завершения задачи");
                return;
            }
            _connector?.SetUpdateState("Обновление: завершение принятых заданий...");
            var drain = await _runtimeServices.Host.RequestDrainAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
            drained = true;
            if (drain.Phase != global::Platform.Connector.Core.ConnectorDrainPhase.ReadyToApply)
            {
                _connector?.SetUpdateState("Обновление: ожидает завершения задачи");
                return;
            }
            var restartBlockReason = GetUpdateRestartBlockReason();
            if (restartBlockReason is not null)
            {
                _connector?.SetUpdateState("Обновление: ожидает завершения задачи");
                ThemedDialogs.Show(this, restartBlockReason, "Обновление", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _packageUpdateService.ApplyUpdatesAndRestart(candidate);
            handedOff = true;
        }
        catch (OperationCanceledException)
        {
            _connector?.SetUpdateState("Обновление: загрузка отменена");
        }
        catch (Exception ex)
        {
            _connector?.SetUpdateState("Обновление: ошибка установки");
            AppendLog("Ошибка пакетного обновления: " + ex.Message);
            ThemedDialogs.Show(this, ex.Message, "Ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (drained && !handedOff) _runtimeServices.Host.Resume();
            _connector?.SetUpdateActionEnabled(_pendingPackageUpdate is not null);
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string GetTeklaTargetDisplayName(string target)
    {
        return target switch
        {
            "extensions" => "Пользовательские приложения",
            "libraries" => "Grasshopper Libraries",
            _ => "Папка фирмы"
        };
    }

    // Public so it satisfies IShellHost (the lifted RestartTeklaServer_Click forwards here) and stays callable
    // by the shell's own Revit-server restart flow. Shared with Revit, so it lives in the shell, not the module.
    public async Task RestartManagedServerAsync(string serviceKey, System.Windows.Controls.Button button, string displayName)
    {
        try
        {
            var canRestart = serviceKey.Equals("tekla", StringComparison.OrdinalIgnoreCase)
                ? (_settings.IsSystemAdmin || _settings.IsFirmAdmin)
                : _settings.IsSystemAdmin;
            if (!canRestart)
            {
                throw new InvalidOperationException(
                    serviceKey.Equals("tekla", StringComparison.OrdinalIgnoreCase)
                        ? "Перезапуск Tekla Server доступен только администратору Tekla или системному администратору."
                        : "Перезапуск Revit Server доступен только системному администратору.");
            }

            var token = SettingsService.DecryptToken(_settings.TokenCipherBase64).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Токен устройства не найден. Выполните подключение по токену.");
            }

            button.IsEnabled = false;
            AppendLog("Запущен перезапуск службы: " + displayName);
            var result = await _heartbeatClient.RestartManagedServiceAsync(_settings.ServerUrl, token, serviceKey, CancellationToken.None);
            AppendLog("Служба перезапущена: " + displayName + "; ответ сервера: " + result.Result.ToString());
            ThemedDialogs.Show(this, 
                displayName + " успешно перезапущен.",
                "Серверные действия",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка перезапуска службы " + displayName + ": " + ex.Message);
            ThemedDialogs.Show(this, ex.Message, "Серверные действия", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            button.IsEnabled = _settings.IsSystemAdmin || _settings.IsFirmAdmin;
        }
    }

    private static string GetSmbHost(string sharePath)
    {
        if (!sharePath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SMB путь должен начинаться с \\\\, например \\\\62.113.36.107\\BIM_Models");
        }

        var parts = sharePath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("SMB путь должен содержать сервер и имя шары.");
        }

        return parts[0];
    }

    private static string GetSmbShareRoot(string sharePath)
    {
        var parts = sharePath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("SMB путь должен содержать сервер и имя шары.");
        }

        return $@"\\{parts[0]}\{parts[1]}";
    }

    private static string NormalizeSmbLogin(string login, string host)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return login;
        }

        if (login.Contains('@'))
        {
            return login;
        }

        if (login.Contains('\\'))
        {
            var parts = login.Split('\\', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var prefix = parts[0].Trim();
                var user = parts[1].Trim();
                if (string.Equals(prefix, host, StringComparison.OrdinalIgnoreCase))
                {
                    return user;
                }
            }
            return login;
        }

        return $"{host}\\{login}";
    }

    private static List<string> BuildSmbLoginCandidates(string login, string host)
    {
        var candidates = new List<string>();

        void Add(string value)
        {
            var v = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(v))
            {
                return;
            }
            if (!candidates.Contains(v, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(v);
            }
        }

        Add(NormalizeSmbLogin(login, host));
        Add(login);

        if (login.Contains('\\'))
        {
            var idx = login.LastIndexOf('\\');
            if (idx >= 0 && idx + 1 < login.Length)
            {
                Add(login[(idx + 1)..]);
            }
        }

        if (!login.Contains('\\') && !login.Contains('@'))
        {
            Add($"{host}\\{login}");
        }

        return candidates;
    }

    private static void ConnectShareWithAnyLogin(string connectionTarget, string shareRoot, string password, IEnumerable<string> loginCandidates)
    {
        Exception? last = null;
        foreach (var candidate in loginCandidates)
        {
            try
            {
                if (string.Equals(connectionTarget, shareRoot, StringComparison.OrdinalIgnoreCase))
                    RunProcessOrThrow("net", "use", shareRoot, password, $"/user:{candidate}", "/persistent:no");
                else
                    RunProcessOrThrow("net", "use", connectionTarget, shareRoot, password, $"/user:{candidate}", "/persistent:no");
                return;
            }
            catch (InvalidOperationException ex)
            {
                last = ex;
            }
        }

        if (last is not null)
        {
            throw last;
        }

        throw new InvalidOperationException("Не удалось выполнить SMB вход: отсутствуют варианты логина.");
    }

    private static (int ExitCode, string Output, string Error) RunProcess(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.GetEncoding(866),
            StandardErrorEncoding = Encoding.GetEncoding(866)
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить процесс.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, NormalizeCliMessage(output), NormalizeCliMessage(error));
    }

    private static void RunProcessOrThrow(string fileName, params string[] args)
    {
        var result = RunProcess(fileName, args);

        if (result.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            throw new InvalidOperationException(details);
        }
    }

    private static string NormalizeCliMessage(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Неизвестная ошибка командной строки.";
        }

        return text.Replace("\r", string.Empty).Trim();
    }

    private static bool IsWindowsSmbConflict(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("1219", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("множественное подключение", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsSmbConflict(Exception ex)
    {
        if (ex is null)
        {
            return false;
        }

        if (IsWindowsSmbConflict(ex.Message))
        {
            return true;
        }

        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.Flatten().InnerExceptions)
            {
                if (IsWindowsSmbConflict(inner))
                {
                    return true;
                }
            }
        }

        return ex.InnerException is not null && IsWindowsSmbConflict(ex.InnerException);
    }

    private static bool IsWindowsNetConnectionNotFound(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("2250", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("не удалось найти сетевое подключение", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsNetNoEntries(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("2250", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("нет записей", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractUncPaths(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var matches = Regex.Matches(text, @"\\\\[^\s]+\\[^\s]+");
        foreach (Match match in matches)
        {
            var path = match.Value.Trim();
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
    }

    private static void DisconnectAllSmbSessionsForHost(string host)
    {
        var list = RunProcess("net", "use");
        var hostPrefix = $@"\\{host}\";
        var hostPaths = ExtractUncPaths(list.Output)
            .Concat(ExtractUncPaths(list.Error))
            .Where(path => path.StartsWith(hostPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var path in hostPaths)
        {
            try
            {
                RunProcessOrThrow("net", "use", path, "/delete", "/y");
            }
            catch (InvalidOperationException ex) when (IsWindowsNetConnectionNotFound(ex.Message) || IsWindowsNetNoEntries(ex.Message))
            {
                // Path already disconnected.
            }
        }

        try
        {
            RunProcessOrThrow("net", "use", hostPrefix + "*", "/delete", "/y");
        }
        catch (InvalidOperationException ex) when (IsWindowsNetConnectionNotFound(ex.Message) || IsWindowsNetNoEntries(ex.Message))
        {
            // Fallback wildcard returned no active entries.
        }

        try
        {
            RunProcessOrThrow("net", "use", $@"\\{host}\IPC$", "/delete", "/y");
        }
        catch (InvalidOperationException ex) when (IsWindowsNetConnectionNotFound(ex.Message) || IsWindowsNetNoEntries(ex.Message))
        {
            // IPC session not present.
        }
    }

    private string ResolveVpnSmbUnc()
    {
        if (!string.IsNullOrWhiteSpace(_settings.VpnSmbUnc))
        {
            return _settings.VpnSmbUnc.Trim();
        }

        return string.IsNullOrWhiteSpace(_settings.VpnServerIp)
            ? string.Empty
            : $@"\\{_settings.VpnServerIp.Trim()}\BIM_Models";
    }

    private static void DeleteStoredWindowsCredentialForHost(string host)
    {
        var targets = new[]
        {
            host,
            $"Microsoft_Windows_Network/{host}"
        };

        foreach (var target in targets)
        {
            var result = RunProcess("cmdkey", $"/delete:{target}");
            if (result.ExitCode == 0)
            {
                continue;
            }

            var details = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            if (details.Contains("не найден", StringComparison.OrdinalIgnoreCase) ||
                details.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
                details.Contains("1168", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }
    }

    private async Task MountVpnShareToDriveAsync(string expectedUnc, string drive)
    {
        var shareRoot = GetSmbShareRoot(expectedUnc);
        var configuredRoot = GetSmbShareRoot(ResolveVpnSmbUnc());
        if (!string.Equals(shareRoot, configuredRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Разрешено только подключение общей папки из VPN-настроек.");

        var normalizedDrive = NormalizeDrive(drive);
        if (_managedSmbMappings.MatchesRecordedTarget(normalizedDrive, shareRoot))
        {
            _connectorManagedSmbDrive = normalizedDrive;
            _connectorManagedSmbShareRoot = shareRoot;
            return;
        }

        var existingDrive = RunProcess("net", "use", normalizedDrive);
        if (DriveInfo.GetDrives().Any(item => string.Equals(item.Name, normalizedDrive + "\\", StringComparison.OrdinalIgnoreCase)) ||
            existingDrive.ExitCode == 0)
            throw new InvalidOperationException($"Диск {normalizedDrive} уже занят; существующее подключение не изменено.");

        var host = GetSmbHost(expectedUnc);
        var loginCandidates = BuildSmbLoginCandidates(_lastSmbLogin, host);
        await Task.Run(() => ConnectShareWithAnyLogin(normalizedDrive, shareRoot, _lastSmbPassword, loginCandidates));
        _managedSmbMappings.Record(normalizedDrive, shareRoot);
        _connectorManagedSmbDrive = normalizedDrive;
        _connectorManagedSmbShareRoot = shareRoot;
    }

    private async Task DisconnectConnectorManagedSmbAsync(string drive)
    {
        var normalizedDrive = NormalizeDrive(drive);
        var assignedShare = GetSmbShareRoot(ResolveVpnSmbUnc());
        if (!_managedSmbMappings.MatchesRecordedTarget(normalizedDrive, assignedShare))
            throw new InvalidOperationException("На этом диске нет подтверждённого подключения коннектора; существующее подключение не изменено.");

        // A drive can be recreated outside Connector with the same UNC. The
        // stored target cannot distinguish that replacement; only this explicit
        // user confirmation authorizes removing the present Windows mapping.
        if (ThemedDialogs.Show(this, $"Отключить общую папку от диска {normalizedDrive}?\n\nБудет отключено текущее сетевое подключение Windows на этом диске.",
            "Общая папка", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            throw new OperationCanceledException();

        if (!_managedSmbMappings.MatchesRecordedTarget(normalizedDrive, assignedShare))
            throw new InvalidOperationException("Подключение изменилось; отключение отменено.");

        await Task.Run(() => RunProcessOrThrow("net", "use", normalizedDrive, "/delete", "/y"));
        _managedSmbMappings.Forget(normalizedDrive);
        _connectorManagedSmbDrive = string.Empty;
        _connectorManagedSmbShareRoot = string.Empty;
        AppendLog("VPN: подключение общей папки отключено.");
    }

    private static string NormalizeDrive(string drive)
    {
        var value = (drive ?? string.Empty).Trim().TrimEnd('\\');
        if (value.Length == 1 && char.IsLetter(value[0])) value += ":";
        if (value.Length != 2 || !char.IsLetter(value[0]) || value[1] != ':')
            throw new InvalidOperationException("Нужна свободная буква диска, например Z:.");
        return char.ToUpperInvariant(value[0]) + ":";
    }

    private async Task ConnectSmbInternalAsync(string login, string password, string sharePath, bool openExplorer)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Введите SMB логин и пароль.");
        }

        var host = GetSmbHost(sharePath);
        var shareRoot = GetSmbShareRoot(sharePath);
        var loginCandidates = BuildSmbLoginCandidates(login, host);
        // SmbLoginTextBox moved into ConnectorView; push the chosen login candidate across the seam (same value).
        _connector?.SetSmbLogin(loginCandidates.FirstOrDefault() ?? login);

        await Task.Run(() =>
        {
            // Only remove a resource this process mounted itself. Existing user
            // sessions and saved Windows credentials must remain untouched.
            if (string.Equals(_connectorManagedSmbShareRoot, shareRoot, StringComparison.OrdinalIgnoreCase))
            {
                try { RunProcessOrThrow("net", "use", shareRoot, "/delete", "/y"); }
                catch (InvalidOperationException ex) when (IsWindowsNetConnectionNotFound(ex.Message) || IsWindowsNetNoEntries(ex.Message)) { }
            }

            try
            {
                ConnectShareWithAnyLogin(shareRoot, shareRoot, password, loginCandidates);
            }
            catch (InvalidOperationException ex) when (IsWindowsSmbConflict(ex.Message))
            {
                throw new InvalidOperationException(
                    $"Windows сохранила активное SMB-подключение к серверу {host} с другими учётными данными. " +
                    "Коннектор не изменил существующие сетевые подключения.", ex);
            }
        });

        _connectorManagedSmbShareRoot = shareRoot;

        AppendLog($"SMB вход выполнен: {shareRoot}");

        if (openExplorer)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = sharePath,
                UseShellExecute = true
            });
        }
    }

    private async void ConnectSmb_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyAndPersist();

            // SmbLogin/SmbPassword/SmbSharePath boxes moved into ConnectorView (Collapsed, button unreachable).
            // Re-source from the in-memory bootstrap creds + _settings (same values the Collapsed boxes carried).
            var login = (_lastSmbLogin ?? string.Empty).Trim();
            var password = (_lastSmbPassword ?? string.Empty).Trim();
            var sharePath = (_settings.SmbSharePath ?? string.Empty).Trim();
            await ConnectSmbInternalAsync(login, password, sharePath, openExplorer: true);
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка SMB входа: " + ex.Message);
            ThemedDialogs.Show(this, ex.Message, "Ошибка SMB входа", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSmbFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // SmbSharePathTextBox moved into ConnectorView (Collapsed, button unreachable); re-source from _settings.
            var sharePath = (_settings.SmbSharePath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(sharePath))
            {
                throw new InvalidOperationException("Укажите путь SMB папки.");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = sharePath,
                UseShellExecute = true
            });

            AppendLog("Открыта SMB папка: " + sharePath);
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка открытия SMB папки: " + ex.Message);
            ThemedDialogs.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
