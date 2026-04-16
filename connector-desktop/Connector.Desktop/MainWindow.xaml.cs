using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Connector.Desktop.Models;
using Connector.Desktop.Services;
using Forms = System.Windows.Forms;

namespace Connector.Desktop;

public partial class MainWindow : Window
{
    private const string FixedServerUrl = "https://server.structura-most.ru";
    private const string FixedUpdateManifestUrl = "https://server.structura-most.ru/updates/latest.json";
    private const int FixedHeartbeatSeconds = 60;
    private const string DefaultSmbSharePath = @"\\62.113.36.107\BIM_Models";
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

    private readonly SettingsService _settingsService = new();
    private readonly AutoStartService _autoStartService = new();
    private readonly HeartbeatClient _heartbeatClient = new(new HttpClient { Timeout = TimeSpan.FromSeconds(110) });
    private readonly UpdateService _updateService = new(new HttpClient { Timeout = TimeSpan.FromSeconds(40) });
    private readonly TeklaStandardService _teklaStandardService = new(new HttpClient { Timeout = TimeSpan.FromSeconds(25) });
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _updateTimer = new();
    private readonly DispatcherTimer _teklaSyncTimer = new();
    private readonly Forms.NotifyIcon _trayIcon;
        private static readonly IReadOnlyList<ReleaseNoteItem> ReleaseNotes = new List<ReleaseNoteItem>
        {
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
    private UpdateToastWindow? _updateToastWindow;
    private string? _downloadedInstallerPath;
    private bool _updateOfferShown;
    private string _lastUpdateToastVersion = string.Empty;
    private bool _updateCheckInProgress;
    private bool _teklaCheckInProgress;
    private bool _teklaBalloonShown;
    private bool _serverConnectionFailed;
    private string _lastTeklaSyncErrorNotice = string.Empty;
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TeklaSyncCheckInterval = TimeSpan.FromMinutes(5);

    private sealed class TeklaManagedTargetState
    {
        public string Key { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string ManifestUrl { get; set; } = "";
        public string LocalPath { get; set; } = "";
        public string InstalledVersion { get; set; } = "";
        public string TargetVersion { get; set; } = "";
        public string InstalledRevision { get; set; } = "";
        public string TargetRevision { get; set; } = "";
        public DateTimeOffset? LastCheckUtc { get; set; }
        public DateTimeOffset? LastSuccessUtc { get; set; }
        public bool PendingAfterClose { get; set; }
        public string LastError { get; set; } = "";
        public string RepoUrl { get; set; } = "";
        public string RepoRef { get; set; } = "";
        public string RepoSubdir { get; set; } = "";
        public TeklaManagedSyncMode SyncMode { get; init; }
        public bool DelayWhenTeklaRunning { get; init; }
    }

    public MainWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _timer.Tick += Timer_Tick;
        _updateTimer.Tick += UpdateTimer_Tick;
        _teklaSyncTimer.Tick += TeklaSyncTimer_Tick;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        StateChanged += MainWindow_StateChanged;
        _trayIcon = CreateTrayIcon();
        LoadSettingsToUi();
        UpdateRunStateUi();
        UpdateActionButtonUi();
        UpdateTeklaUi();
        UpdateHeaderStatusUi();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Topmost = true;
        Activate();
        Focus();
        Topmost = false;
        AppendLog("При закрытии окно сворачивается в трей. Для полного выхода: иконка в трее -> Закрыть.");
        if (_teklaStandardService.CheckGitAvailability(out var gitPath, out var gitDetails))
        {
            AppendLog("Стандарт Tekla: git доступен (" + gitPath + ") " + gitDetails);
        }
        else
        {
            AppendLog("Стандарт Tekla: git недоступен (" + gitPath + ") " + gitDetails);
        }
        _ = TryAutoConnectAsync();
        _ = CheckUpdatesAsync(showDialogs: false);
        _ = RunTeklaSyncCycleAsync(showDialogs: false, forceRefresh: false, autoApplyIfPossible: true);
        _updateTimer.Interval = UpdateCheckInterval;
        _updateTimer.Start();
        _teklaSyncTimer.Interval = TeklaSyncCheckInterval;
        _teklaSyncTimer.Start();
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

        if (_pendingUpdate is null)
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
            UpdateStateTextBlock.Text = "Обновление: загрузка установщика...";
            _downloadedInstallerPath = await _updateService.DownloadInstallerAsync(_pendingUpdate, CancellationToken.None);
            AppendLog("Скачан установщик обновления: " + _downloadedInstallerPath);
            UpdateService.RunInstaller(_downloadedInstallerPath);
            ExitFromTray();
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка автообновления: " + ex.Message);
            UpdateStateTextBlock.Text = "Обновление: ошибка установки";
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

    private async Task TryAutoConnectAsync()
    {
        try
        {
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

    private Forms.NotifyIcon CreateTrayIcon()
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
            Visible = true,
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

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _updateTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private async void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        await CheckUpdatesAsync(showDialogs: false);
        await RunTeklaSyncCycleAsync(showDialogs: false, forceRefresh: false, autoApplyIfPossible: true);
    }

    private async void TeklaSyncTimer_Tick(object? sender, EventArgs e)
    {
        await RunTeklaSyncCycleAsync(showDialogs: false, forceRefresh: false, autoApplyIfPossible: true);
    }

    private void UpdateActionButtonUi()
    {
        if (_pendingUpdate is null)
        {
            UpdateActionButton.Content = "Проверить обновление коннектора";
            UpdateActionButton.Style = (Style)FindResource("SecondaryButton");
            return;
        }

        UpdateActionButton.Content = "Скачать и установить обновление";
        UpdateActionButton.Style = (Style)FindResource("PrimaryButton");
    }

    private void UpdateTeklaUi()
    {
        var firmTargetRevision = string.IsNullOrWhiteSpace(_settings.TeklaStandardTargetRevision)
            ? "-"
            : _settings.TeklaStandardTargetRevision.Trim();
        var firmHasTargetRevision = !string.IsNullOrWhiteSpace(_settings.TeklaStandardTargetRevision);
        TeklaCurrentVersionTextBlock.Text = firmTargetRevision;
        TeklaUpToDateTextBlock.Text = firmHasTargetRevision &&
                                      !_teklaStandardService.IsUpdateAvailable(_settings.TeklaStandardInstalledRevision, _settings.TeklaStandardTargetRevision)
            ? "да"
            : "нет";
        TeklaStatusTextBlock.Text = BuildFirmStatusText();
        TeklaRoleTextBlock.Text = _settings.IsFirmAdmin ? "Роль администратора: да" : "Роль администратора: нет";
        TeklaFirmLocalPathTextBox.Text = string.IsNullOrWhiteSpace(_settings.TeklaStandardLocalPath)
            ? DefaultTeklaStandardLocalPath
            : _settings.TeklaStandardLocalPath;
        TeklaExtensionsCurrentVersionTextBlock.Text = string.IsNullOrWhiteSpace(_settings.TeklaExtensionsTargetRevision)
            ? "-"
            : _settings.TeklaExtensionsTargetRevision.Trim();
        TeklaExtensionsUpToDateTextBlock.Text = !string.IsNullOrWhiteSpace(_settings.TeklaExtensionsTargetRevision) &&
                                                !_teklaStandardService.IsUpdateAvailable(_settings.TeklaExtensionsInstalledRevision, _settings.TeklaExtensionsTargetRevision)
            ? "да"
            : "нет";
        TeklaExtensionsStatusTextBlock.Text = BuildExtensionsStatusText();
        TeklaExtensionsLocalPathTextBox.Text = string.IsNullOrWhiteSpace(_settings.TeklaExtensionsLocalPath)
            ? DefaultTeklaExtensionsLocalPath
            : _settings.TeklaExtensionsLocalPath;
        TeklaLibrariesCurrentVersionTextBlock.Text = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesTargetRevision)
            ? "-"
            : _settings.TeklaLibrariesTargetRevision.Trim();
        TeklaLibrariesUpToDateTextBlock.Text = !string.IsNullOrWhiteSpace(_settings.TeklaLibrariesTargetRevision) &&
                                               !_teklaStandardService.IsUpdateAvailable(_settings.TeklaLibrariesInstalledRevision, _settings.TeklaLibrariesTargetRevision)
            ? "да"
            : "нет";
        TeklaLibrariesStatusTextBlock.Text = BuildLibrariesStatusText();
        TeklaLibrariesLocalPathTextBox.Text = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesLocalPath)
            ? DefaultTeklaLibrariesLocalPath
            : _settings.TeklaLibrariesLocalPath;
        TeklaPublishFirmSourcePathTextBox.Text = string.IsNullOrWhiteSpace(_settings.TeklaPublishSourcePath)
            ? DefaultTeklaPublishSourcePath
            : _settings.TeklaPublishSourcePath;
        TeklaPublishExtensionsSourcePathTextBox.Text = string.IsNullOrWhiteSpace(_settings.TeklaExtensionsPublishSourcePath)
            ? DefaultTeklaExtensionsPublishSourcePath
            : _settings.TeklaExtensionsPublishSourcePath;
        TeklaPublishLibrariesSourcePathTextBox.Text = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesPublishSourcePath)
            ? DefaultTeklaLibrariesPublishSourcePath
            : _settings.TeklaLibrariesPublishSourcePath;
        UpdateStructuraAccessStatusUi();
        UpdateTeklaActionButtonUi();

        TeklaPublishPanel.Visibility = _settings.IsFirmAdmin ? Visibility.Visible : Visibility.Collapsed;
        TeklaPublishButton.IsEnabled = _settings.IsFirmAdmin;
        if (string.IsNullOrWhiteSpace(TeklaPublishNotesTextBox.Text))
        {
            TeklaPublishNotesTextBox.Text = "Публикация из Structura Connector";
        }

        var canRestartTeklaServer = _settings.IsSystemAdmin || _settings.IsFirmAdmin;
        ServerActionsPanel.Visibility = canRestartTeklaServer ? Visibility.Visible : Visibility.Collapsed;
        RestartTeklaServerButton.IsEnabled = canRestartTeklaServer;
        var (teklaOverallText, teklaOverallBrush) = BuildTeklaOverallStatus();
        TeklaOverallStatusTextBlock.Text = teklaOverallText;
        TeklaOverallStatusTextBlock.Foreground = teklaOverallBrush;
        ConnectorTeklaSyncStatusTextBlock.Text = teklaOverallText;
        ConnectorTeklaSyncStatusTextBlock.Foreground = teklaOverallBrush;
        UpdateHeaderStatusUi();
    }

    private void UpdateTeklaActionButtonUi()
    {
        if (_teklaCheckInProgress)
        {
            const string inProgressText = "Идет синхронизация Tekla...";
            TeklaCheckButton.Content = inProgressText;
            TeklaCheckButton.Style = (Style)FindResource("SyncButton");
            TeklaCheckButton.IsEnabled = true;
            ConnectorTeklaSyncButton.Content = inProgressText;
            ConnectorTeklaSyncButton.Style = (Style)FindResource("SyncButton");
            ConnectorTeklaSyncButton.IsEnabled = true;
            return;
        }

        var hasUpdate =
            _teklaStandardService.IsUpdateAvailable(_settings.TeklaStandardInstalledRevision, _settings.TeklaStandardTargetRevision) ||
            _teklaStandardService.IsUpdateAvailable(_settings.TeklaExtensionsInstalledRevision, _settings.TeklaExtensionsTargetRevision) ||
            _teklaStandardService.IsUpdateAvailable(_settings.TeklaLibrariesInstalledRevision, _settings.TeklaLibrariesTargetRevision);
        var hasError = !string.IsNullOrWhiteSpace(FirstNonEmpty(
            _settings.TeklaStandardLastError,
            _settings.TeklaExtensionsLastError,
            _settings.TeklaLibrariesLastError));

        TeklaCheckButton.Content = hasError
            ? "Повторить синхронизацию Tekla"
            : hasUpdate
                ? "Обновить Tekla сейчас"
                : "Проверить и синхронизировать Tekla";
        TeklaCheckButton.Style = (Style)FindResource(hasUpdate || hasError ? "SyncButton" : "SecondaryButton");
        TeklaCheckButton.IsEnabled = true;
        ConnectorTeklaSyncButton.Content = (string)TeklaCheckButton.Content;
        ConnectorTeklaSyncButton.Style = (Style)FindResource(hasUpdate || hasError ? "SyncButton" : "SecondaryButton");
        ConnectorTeklaSyncButton.IsEnabled = true;
    }

    private void UpdateStructuraAccessStatusUi()
    {
        var hasSpeckle = !string.IsNullOrWhiteSpace(_settings.StructuraSpeckleUrl);
        var hasNextcloud = !string.IsNullOrWhiteSpace(_settings.StructuraNextcloudUrl);
        var hasAnyLogin =
            !string.IsNullOrWhiteSpace(_settings.StructuraSpeckleLogin) ||
            !string.IsNullOrWhiteSpace(_settings.StructuraNextcloudLogin);

        StructuraAccessStatusTextBlock.Text = hasSpeckle || hasNextcloud
            ? hasAnyLogin
                ? "Structura: доступы получены с сервера и привязаны к текущему токену"
                : "Structura: ссылки получены, логины еще не назначены в админке"
            : "Structura: доступы еще не получены с сервера";
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

        var (teklaOverallText, teklaOverallBrush) = BuildTeklaOverallStatus();
        HeaderFirmStatusTextBlock.Text = teklaOverallText;
        HeaderFirmStatusTextBlock.Foreground = teklaOverallBrush;
    }

    private (string Text, System.Windows.Media.Brush Brush) BuildTeklaOverallStatus()
    {
        var hasKnownState = false;
        var outdated = new List<string>();
        var errors = new List<string>();

        void Collect(string name, string installedRevision, string targetRevision, string lastError, bool pendingAfterClose)
        {
            if (!string.IsNullOrWhiteSpace(installedRevision) || !string.IsNullOrWhiteSpace(targetRevision))
            {
                hasKnownState = true;
            }

            if (pendingAfterClose || _teklaStandardService.IsUpdateAvailable(installedRevision, targetRevision))
            {
                outdated.Add(name);
            }

            if (string.IsNullOrWhiteSpace(lastError))
            {
                return;
            }

            if (string.Equals(lastError, "manifest_not_received", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(name + " (нет связи с сервером обновлений)");
                return;
            }

            errors.Add(name);
        }

        Collect(
            "папка фирмы",
            _settings.TeklaStandardInstalledRevision,
            _settings.TeklaStandardTargetRevision,
            _settings.TeklaStandardLastError,
            _settings.TeklaStandardPendingAfterClose);
        Collect(
            "пользовательские приложения",
            _settings.TeklaExtensionsInstalledRevision,
            _settings.TeklaExtensionsTargetRevision,
            _settings.TeklaExtensionsLastError,
            _settings.TeklaExtensionsPendingAfterClose);
        Collect(
            "Grasshopper Libraries",
            _settings.TeklaLibrariesInstalledRevision,
            _settings.TeklaLibrariesTargetRevision,
            _settings.TeklaLibrariesLastError,
            _settings.TeklaLibrariesPendingAfterClose);

        if (!hasKnownState)
        {
            return ("Tekla Sync: проверка еще не выполнялась", System.Windows.Media.Brushes.DarkGray);
        }

        if (errors.Count > 0)
        {
            return ("Tekla Sync: есть ошибки в разделах: " + string.Join(", ", errors), System.Windows.Media.Brushes.Orange);
        }

        if (outdated.Count > 0)
        {
            return ("Tekla Sync: требуется обновить: " + string.Join(", ", outdated), System.Windows.Media.Brushes.Orange);
        }

        return ("Tekla Sync: все разделы актуальны", System.Windows.Media.Brushes.MediumSpringGreen);
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

    private void ShowTeklaSyncFailedBalloon(string targetName, string message)
    {
        var noticeKey = targetName + "|" + message;
        if (string.Equals(_lastTeklaSyncErrorNotice, noticeKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastTeklaSyncErrorNotice = noticeKey;
        var balloonMessage = string.IsNullOrWhiteSpace(message)
            ? targetName + ": не удалось синхронизировать. Закройте блокирующую программу и повторите синхронизацию."
            : message;
        if (balloonMessage.Length > 240)
        {
            balloonMessage = balloonMessage[..237] + "...";
        }
        _trayIcon.ShowBalloonTip(
            5000,
            "Стандарт Tekla",
            balloonMessage,
            Forms.ToolTipIcon.Warning);
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

    private void ExitFromTray()
    {
        _allowClose = true;
        _timer.Stop();
        _updateTimer.Stop();
        _isRunning = false;
        Close();
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
        _settings.AutoStart = true;
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

        ServerUrlTextBox.Text = _settings.ServerUrl;
        UpdateManifestUrlTextBox.Text = _settings.UpdateManifestUrl;
        DeviceIdTextBox.Text = _settings.DeviceId;
        SmbLoginTextBox.Text = string.Empty;
        SmbSharePathTextBox.Text = _settings.SmbSharePath;
        IntervalTextBox.Text = _settings.HeartbeatSeconds.ToString();
        AutoStartCheckBox.IsChecked = true;
        TeklaPublishFirmSourcePathTextBox.Text = _settings.TeklaPublishSourcePath;
        TeklaPublishExtensionsSourcePathTextBox.Text = _settings.TeklaExtensionsPublishSourcePath;
        TeklaPublishLibrariesSourcePathTextBox.Text = _settings.TeklaLibrariesPublishSourcePath;
        TeklaFirmLocalPathTextBox.Text = _settings.TeklaStandardLocalPath;
        TeklaExtensionsLocalPathTextBox.Text = _settings.TeklaExtensionsLocalPath;
        TeklaLibrariesLocalPathTextBox.Text = _settings.TeklaLibrariesLocalPath;
        UpdateStructuraAccessStatusUi();

        var token = SettingsService.DecryptToken(_settings.TokenCipherBase64);
        TokenPasswordBox.Password = token;

        SmbPasswordBox.Password = string.Empty;

        _timer.Interval = TimeSpan.FromSeconds(_settings.HeartbeatSeconds);

        if (shouldPersist)
        {
            _settingsService.Save(_settings);
        }

        AppendLog($"Настройки загружены: {_settingsService.SettingsPath}");
    }

    private AppSettings ReadSettingsFromUi()
    {
        var token = TokenPasswordBox.Password.Trim();
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
        var teklaLocalPath = string.IsNullOrWhiteSpace(TeklaFirmLocalPathTextBox.Text)
            ? (string.IsNullOrWhiteSpace(_settings.TeklaStandardLocalPath)
                ? DefaultTeklaStandardLocalPath
                : _settings.TeklaStandardLocalPath)
            : TeklaFirmLocalPathTextBox.Text.Trim();
        var teklaExtensionsManifestUrl = string.IsNullOrWhiteSpace(_settings.TeklaExtensionsManifestUrl)
            ? FixedTeklaExtensionsManifestUrl
            : _settings.TeklaExtensionsManifestUrl;
        var teklaExtensionsLocalPath = string.IsNullOrWhiteSpace(TeklaExtensionsLocalPathTextBox.Text)
            ? (string.IsNullOrWhiteSpace(_settings.TeklaExtensionsLocalPath)
                ? DefaultTeklaExtensionsLocalPath
                : _settings.TeklaExtensionsLocalPath)
            : TeklaExtensionsLocalPathTextBox.Text.Trim();
        var teklaLibrariesManifestUrl = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesManifestUrl)
            ? FixedTeklaLibrariesManifestUrl
            : _settings.TeklaLibrariesManifestUrl;
        var teklaLibrariesLocalPath = string.IsNullOrWhiteSpace(TeklaLibrariesLocalPathTextBox.Text)
            ? (string.IsNullOrWhiteSpace(_settings.TeklaLibrariesLocalPath)
                ? DefaultTeklaLibrariesLocalPath
                : _settings.TeklaLibrariesLocalPath)
            : TeklaLibrariesLocalPathTextBox.Text.Trim();
        var teklaFirmPublishSourcePath = string.IsNullOrWhiteSpace(TeklaPublishFirmSourcePathTextBox.Text)
            ? DefaultTeklaPublishSourcePath
            : TeklaPublishFirmSourcePathTextBox.Text.Trim();
        var teklaExtensionsPublishSourcePath = string.IsNullOrWhiteSpace(TeklaPublishExtensionsSourcePathTextBox.Text)
            ? DefaultTeklaExtensionsPublishSourcePath
            : TeklaPublishExtensionsSourcePathTextBox.Text.Trim();
        var teklaLibrariesPublishSourcePath = string.IsNullOrWhiteSpace(TeklaPublishLibrariesSourcePathTextBox.Text)
            ? DefaultTeklaLibrariesPublishSourcePath
            : TeklaPublishLibrariesSourcePathTextBox.Text.Trim();

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
            AutoStart = true,
            TeklaStandardManifestUrl = teklaManifestUrl,
            TeklaStandardLocalPath = teklaLocalPath,
            TeklaStandardInstalledRevision = _settings.TeklaStandardInstalledRevision,
            TeklaStandardTargetRevision = _settings.TeklaStandardTargetRevision,
            TeklaStandardLastCheckUtc = _settings.TeklaStandardLastCheckUtc,
            TeklaStandardLastSuccessUtc = _settings.TeklaStandardLastSuccessUtc,
            TeklaStandardPendingAfterClose = _settings.TeklaStandardPendingAfterClose,
            TeklaStandardLastError = _settings.TeklaStandardLastError,
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
            IsFirmAdmin = _settings.IsFirmAdmin
        };
    }

    private void ApplyAndPersist()
    {
        _settings = ReadSettingsFromUi();
        _settingsService.Save(_settings);
        _autoStartService.SetEnabled(_settings.AutoStart);
        _timer.Interval = TimeSpan.FromSeconds(_settings.HeartbeatSeconds);
        UpdateTeklaUi();
        AppendLog("Настройки сохранены.");
    }

    private void UpdateRunStateUi()
    {
        if (_isRunning)
        {
            RunStateTextBlock.Text = "Автоотправка heartbeat: включена";
            RunStateTextBlock.Foreground = System.Windows.Media.Brushes.MediumSpringGreen;
        }
        else
        {
            RunStateTextBlock.Text = "Автоотправка heartbeat: выключена";
            RunStateTextBlock.Foreground = System.Windows.Media.Brushes.Orange;
        }

        StartButton.IsEnabled = !_isRunning;
        StopButton.IsEnabled = _isRunning;
        UpdateHeaderStatusUi();
    }

    private async Task SendHeartbeatSafeAsync()
    {
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
                CancellationToken.None);
            _serverConnectionFailed = false;
            AppendLog("Heartbeat отправлен успешно.");
            UpdateHeaderStatusUi();
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("HTTP 409", StringComparison.OrdinalIgnoreCase))
            {
                _timer.Stop();
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

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        await SendHeartbeatSafeAsync();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyAndPersist();
            _timer.Start();
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
        _timer.Stop();
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
            var serverUrl = ServerUrlTextBox.Text.Trim();
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

    private void OpenSpeckle_Click(object sender, RoutedEventArgs e)
    {
        OpenStructuraWebPage(_settings.StructuraSpeckleUrl, "Speckle");
    }

    private void OpenNextcloud_Click(object sender, RoutedEventArgs e)
    {
        OpenStructuraWebPage(_settings.StructuraNextcloudUrl, "Nextcloud");
    }

    private void ShowSpeckleAccess_Click(object sender, RoutedEventArgs e)
    {
        ShowStructuraAccessWindow(
            "Сервер моделей - Speckle",
            _settings.StructuraSpeckleUrl,
            _settings.StructuraSpeckleLogin,
            _settings.StructuraSpecklePasswordCipherBase64);
    }

    private void ShowNextcloudAccess_Click(object sender, RoutedEventArgs e)
    {
        ShowStructuraAccessWindow(
            "Среда взаимодействия - Nextcloud",
            _settings.StructuraNextcloudUrl,
            _settings.StructuraNextcloudLogin,
            _settings.StructuraNextcloudPasswordCipherBase64);
    }

    private void ShowStructuraAccessWindow(string title, string domain, string login, string passwordCipherBase64)
    {
        var dialog = new StructuraAccessWindow(
            title,
            domain,
            login,
            SettingsService.DecryptToken(passwordCipherBase64))
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void OpenStructuraWebPage(string url, string label)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Некорректная ссылка " + label + ".");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true
        });
        AppendLog("Открыта страница " + label + ": " + uri);
    }

    private async void ConnectByToken_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var token = TokenPasswordBox.Password.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Введите токен устройства.");
            }

            await ConnectByTokenInternalAsync(token, showSuccessDialog: true);
        }
        catch (TaskCanceledException)
        {
            const string message = "Сервер отвечает дольше обычного. Подождите немного и повторите подключение.";
            AppendLog("Ошибка автоподключения по токену: " + message);
            _serverConnectionFailed = true;
            UpdateHeaderStatusUi();
            ThemedDialogs.Show(this, message, "Время ожидания истекло", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка автоподключения по токену: " + ex.Message);
            _serverConnectionFailed = true;
            UpdateHeaderStatusUi();
            ThemedDialogs.Show(this, ex.Message, "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ConnectByTokenInternalAsync(string token, bool showSuccessDialog)
    {
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
            AutoStart = true,
            TeklaStandardManifestUrl = string.IsNullOrWhiteSpace(_settings.TeklaStandardManifestUrl)
                ? FixedTeklaStandardManifestUrl
                : _settings.TeklaStandardManifestUrl,
            TeklaStandardLocalPath = string.IsNullOrWhiteSpace(_settings.TeklaStandardLocalPath)
                ? DefaultTeklaStandardLocalPath
                : _settings.TeklaStandardLocalPath,
            TeklaStandardInstalledRevision = _settings.TeklaStandardInstalledRevision,
            TeklaStandardTargetRevision = _settings.TeklaStandardTargetRevision,
            TeklaStandardLastCheckUtc = _settings.TeklaStandardLastCheckUtc,
            TeklaStandardLastSuccessUtc = _settings.TeklaStandardLastSuccessUtc,
            TeklaStandardPendingAfterClose = _settings.TeklaStandardPendingAfterClose,
            TeklaStandardLastError = _settings.TeklaStandardLastError,
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
            IsFirmAdmin = bootstrap.IsFirmAdmin
        };
        _settingsService.Save(_settings);
        _autoStartService.SetEnabled(true);
        _timer.Interval = TimeSpan.FromSeconds(_settings.HeartbeatSeconds);
        _activeSessionId = bootstrap.SessionId;
        _serverConnectionFailed = false;

        DeviceIdTextBox.Text = _settings.DeviceId;
        IntervalTextBox.Text = _settings.HeartbeatSeconds.ToString();
        UpdateManifestUrlTextBox.Text = _settings.UpdateManifestUrl;
        SmbLoginTextBox.Text = bootstrap.SmbAccess.Login;
        SmbPasswordBox.Password = string.Empty;
        SmbSharePathTextBox.Text = _settings.SmbSharePath;
        UpdateTeklaUi();
        AppendLog("Настройки сохранены.");

        var smbConnected = true;
        try
        {
            await ConnectSmbInternalAsync(bootstrap.SmbAccess.Login, bootstrap.SmbAccess.Password, sharePath, openExplorer: true);
        }
        catch (Exception ex) when (IsWindowsSmbConflict(ex))
        {
            smbConnected = false;
            AppendLog("SMB подключение не переключено автоматически (конфликт 1219). Текущая сессия SMB оставлена без изменений.");
            AppendLog("Детали SMB конфликта: " + ex.Message);
        }

        _timer.Stop();
        _timer.Start();
        _isRunning = true;
        UpdateRunStateUi();

        await SendHeartbeatSafeAsync();
        AppendLog("Автоподключение по токену выполнено успешно.");

        if (showSuccessDialog)
        {
            ThemedDialogs.Show(this, 
                smbConnected
                    ? "Подключение выполнено. SMB доступ открыт, автоотправка heartbeat включена."
                    : "Подключение выполнено. Автоотправка heartbeat включена, но SMB не переключен из-за активной сессии Windows (1219).",
                "Structura Connector",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private async Task CheckUpdatesAsync(bool showDialogs)
    {
        if (_updateCheckInProgress)
        {
            return;
        }

        _updateCheckInProgress = true;
        UpdateActionButton.IsEnabled = false;
        try
        {
            var manifestUrl = string.IsNullOrWhiteSpace(_settings.UpdateManifestUrl)
                ? FixedUpdateManifestUrl
                : _settings.UpdateManifestUrl.Trim();
            if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException("Введите корректный адрес обновлений.");
            }

            _settings.UpdateManifestUrl = manifestUrl;
            UpdateManifestUrlTextBox.Text = manifestUrl;
            _settingsService.Save(_settings);

            var manifest = await _updateService.TryGetUpdateAsync(manifestUrl, CancellationToken.None);
            if (manifest is null)
            {
                _pendingUpdate = null;
                _lastUpdateToastVersion = string.Empty;
                UpdateStateTextBlock.Text = "Обновление: не удалось получить данные";
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
                UpdateStateTextBlock.Text = $"Доступно обновление: {manifest.Version}";
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
                UpdateStateTextBlock.Text = $"Обновление: актуально ({_updateService.CurrentVersion})";
                UpdateActionButtonUi();
                if (showDialogs)
                {
                    ThemedDialogs.Show(this, "Установлена актуальная версия.", "Обновления", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            _pendingUpdate = null;
            UpdateStateTextBlock.Text = "Обновление: ошибка проверки";
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
            UpdateActionButton.IsEnabled = true;
        }
    }

    private async void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null)
        {
            await CheckUpdatesAsync(showDialogs: true);
            return;
        }

        await InstallPendingUpdateAsync(confirmBeforeRun: true);
    }

    private void ShowReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        var window = new ReleaseNotesWindow(ReleaseNotes, "1.0.16")
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private async Task InstallPendingUpdateAsync(bool confirmBeforeRun)
    {
        try
        {
            if (_pendingUpdate is null)
            {
                await CheckUpdatesAsync(showDialogs: true);
                if (_pendingUpdate is null)
                {
                    return;
                }
            }

            UpdateActionButton.IsEnabled = false;
            UpdateStateTextBlock.Text = "Обновление: загрузка установщика...";
            _downloadedInstallerPath = await _updateService.DownloadInstallerAsync(_pendingUpdate, CancellationToken.None);
            AppendLog("Скачан установщик обновления: " + _downloadedInstallerPath);

            var shouldRunInstaller = !confirmBeforeRun || ThemedDialogs.Show(this,
                "Установщик скачан. Закрыть приложение и запустить обновление сейчас?",
                "Обновление",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;

            if (shouldRunInstaller)
            {
                UpdateService.RunInstaller(_downloadedInstallerPath);
                ExitFromTray();
            }
            else
            {
                UpdateStateTextBlock.Text = "Обновление: установщик скачан";
                UpdateActionButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            UpdateActionButton.IsEnabled = _pendingUpdate is not null;
            UpdateStateTextBlock.Text = "Обновление: ошибка установки";
            AppendLog("Ошибка установки обновления: " + ex.Message);
            ThemedDialogs.Show(this, ex.Message, "Ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<List<string>> RunTeklaSyncCycleAsync(
        bool showDialogs,
        bool forceRefresh,
        bool autoApplyIfPossible,
        OperationProgressWindow? progressReporter = null)
    {
        var summaryLines = new List<string>();
        if (_teklaCheckInProgress)
        {
            summaryLines.Add("Синхронизация уже выполняется");
            return summaryLines;
        }

        _teklaCheckInProgress = true;
        UpdateTeklaActionButtonUi();

        try
        {
            NormalizeTeklaSettings();
            ApplyAndPersistTeklaPathsOnly();

            const int totalSteps = 3;
            var currentStep = 0;

            currentStep++;
            progressReporter?.UpdateStep(
                "Проверяем раздел: Папка фирмы",
                "Получаем данные и применяем обновление при необходимости",
                currentStep,
                totalSteps,
                EstimateOperationEta(currentStep, totalSteps));
            await CheckAndApplyTeklaTargetAsync(CreateFirmTargetState(), ApplyFirmTargetState, forceRefresh, autoApplyIfPossible, summaryLines, progressReporter);

            currentStep++;
            progressReporter?.UpdateStep(
                "Проверяем раздел: Пользовательские приложения",
                "Получаем данные и применяем обновление при необходимости",
                currentStep,
                totalSteps,
                EstimateOperationEta(currentStep, totalSteps));
            await CheckAndApplyTeklaTargetAsync(CreateExtensionsTargetState(), ApplyExtensionsTargetState, forceRefresh, autoApplyIfPossible, summaryLines, progressReporter);

            currentStep++;
            progressReporter?.UpdateStep(
                "Проверяем раздел: Grasshopper Libraries",
                "Получаем данные и применяем обновление при необходимости",
                currentStep,
                totalSteps,
                EstimateOperationEta(currentStep, totalSteps));
            await CheckAndApplyTeklaTargetAsync(CreateLibrariesTargetState(), ApplyLibrariesTargetState, forceRefresh, autoApplyIfPossible, summaryLines, progressReporter);

            _settingsService.Save(_settings);
            UpdateTeklaUi();

            if (showDialogs)
            {
                var message = summaryLines.Count == 0
                    ? "Проверка Tekla sync завершена"
                    : string.Join(Environment.NewLine, summaryLines);
                ThemedDialogs.Show(this, message, "Стандарт Tekla", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _settings.TeklaStandardLastError = ex.Message;
            _settings.TeklaExtensionsLastError = ex.Message;
            _settings.TeklaLibrariesLastError = ex.Message;
            _settingsService.Save(_settings);
            summaryLines.Add("Синхронизация завершилась с ошибкой: " + ex.Message);
            AppendLog("Ошибка проверки Tekla sync: " + ex.Message);
            if (showDialogs)
            {
                ThemedDialogs.Show(this, ex.Message, "Стандарт Tekla", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _teklaCheckInProgress = false;
            UpdateTeklaUi();
        }

        return summaryLines;
    }

    private async Task CheckAndApplyTeklaTargetAsync(
        TeklaManagedTargetState target,
        Action<TeklaManagedTargetState> persist,
        bool forceRefresh,
        bool autoApplyIfPossible,
        List<string> summaryLines,
        OperationProgressWindow? progressReporter = null)
    {
        if (forceRefresh)
        {
            target.TargetRevision = string.Empty;
        }

        var manifest = await _teklaStandardService.TryGetManifestAsync(target.ManifestUrl, CancellationToken.None);
        target.LastCheckUtc = DateTimeOffset.UtcNow;

        if (manifest is null)
        {
            target.TargetVersion = string.Empty;
            target.TargetRevision = string.Empty;
            target.LastError = "manifest_not_received";
            target.PendingAfterClose = false;
            persist(target);
            summaryLines.Add(target.DisplayName + ": данные обновления недоступны");
            return;
        }

        target.TargetVersion = manifest.Version;
        target.TargetRevision = manifest.Revision;
        target.RepoUrl = manifest.RepoUrl;
        target.RepoRef = manifest.RepoRef;
        target.RepoSubdir = manifest.RepoSubdir;

        var updateAvailable = _teklaStandardService.IsUpdateAvailable(target.InstalledRevision, target.TargetRevision);
        if (!updateAvailable)
        {
            target.PendingAfterClose = false;
            target.LastError = string.Empty;
            _lastTeklaSyncErrorNotice = string.Empty;
            persist(target);
            if (target.Key == "firm")
            {
                _teklaBalloonShown = false;
            }
            summaryLines.Add(target.DisplayName + ": актуально (" + target.TargetRevision + ")");
            return;
        }

        AppendLog(target.DisplayName + ": найдена новая ревизия " + target.TargetRevision);

        if (!autoApplyIfPossible)
        {
            target.PendingAfterClose = false;
            target.LastError = string.Empty;
            persist(target);
            summaryLines.Add(target.DisplayName + ": доступна ревизия " + target.TargetRevision);
            return;
        }

        progressReporter?.UpdateDetail("Копируем обновления в локальную папку: " + target.DisplayName);
        var applyResult = await Task.Run(() => _teklaStandardService.ApplyPendingGitUpdate(new TeklaManagedSyncRequest
        {
            TargetKey = target.Key,
            DisplayName = target.DisplayName,
            RepoUrl = target.RepoUrl,
            RepoRef = target.RepoRef,
            RepoSubdir = target.RepoSubdir,
            LocalPath = target.LocalPath,
            TargetRevision = target.TargetRevision,
            Mode = target.SyncMode
        }));

        if (applyResult.IsSuccess)
        {
            _teklaStandardService.AppendLog(applyResult.Message);
            AppendLog(applyResult.Message);
            target.InstalledVersion = target.TargetVersion;
            target.InstalledRevision = applyResult.InstalledRevision;
            target.LastSuccessUtc = DateTimeOffset.UtcNow;
            target.PendingAfterClose = false;
            target.LastError = string.Empty;
            _lastTeklaSyncErrorNotice = string.Empty;
            if (target.Key == "firm")
            {
                _teklaBalloonShown = false;
            }
            persist(target);
            summaryLines.Add(target.DisplayName + ": синхронизация выполнена");
            return;
        }

        target.LastError = applyResult.Message;
        target.PendingAfterClose = false;
        persist(target);
        if (autoApplyIfPossible && !string.IsNullOrWhiteSpace(applyResult.Message))
        {
            ShowTeklaSyncFailedBalloon(target.DisplayName, applyResult.Message);
        }
        summaryLines.Add(target.DisplayName + ": ошибка применения");
    }

    private async void TeklaUpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_teklaCheckInProgress)
        {
            ThemedDialogs.Show(
                this,
                "Синхронизация уже выполняется. Дождитесь завершения текущей операции",
                "Стандарт Tekla",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var progressWindow = new OperationProgressWindow("Синхронизация Tekla", "Проверяем и обновляем локальные папки");
        progressWindow.Owner = this;
        progressWindow.Show();
        try
        {
            var summaryLines = await RunTeklaSyncCycleAsync(
                showDialogs: false,
                forceRefresh: true,
                autoApplyIfPossible: true,
                progressReporter: progressWindow);

            progressWindow.MarkSucceeded(summaryLines.Count == 0
                ? "Синхронизация завершена"
                : string.Join(Environment.NewLine, summaryLines));
        }
        catch (Exception ex)
        {
            progressWindow.MarkFailed(ex.Message);
        }
    }

    private void TeklaOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenTeklaFolder(
            string.IsNullOrWhiteSpace(_settings.TeklaStandardLocalPath) ? DefaultTeklaStandardLocalPath : _settings.TeklaStandardLocalPath.Trim(),
            "Папка фирмы Tekla");
    }

    private void TeklaFirmBrowse_Click(object sender, RoutedEventArgs e)
    {
        BrowseLocalTeklaFolder(
            "Выберите локальную папку фирмы Tekla",
            TeklaFirmLocalPathTextBox,
            path => _settings.TeklaStandardLocalPath = path,
            "папки фирмы");
    }

    private void TeklaExtensionsOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenTeklaFolder(
            string.IsNullOrWhiteSpace(_settings.TeklaExtensionsLocalPath) ? DefaultTeklaExtensionsLocalPath : _settings.TeklaExtensionsLocalPath.Trim(),
            "Extensions Tekla");
    }

    private void TeklaExtensionsBrowse_Click(object sender, RoutedEventArgs e)
    {
        BrowseLocalTeklaFolder(
            "Выберите локальную папку Extensions для Tekla",
            TeklaExtensionsLocalPathTextBox,
            path => _settings.TeklaExtensionsLocalPath = path,
            "Extensions");
    }

    private void TeklaLibrariesOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenTeklaFolder(
            string.IsNullOrWhiteSpace(_settings.TeklaLibrariesLocalPath) ? DefaultTeklaLibrariesLocalPath : _settings.TeklaLibrariesLocalPath.Trim(),
            "Grasshopper Libraries");
    }

    private void TeklaLibrariesBrowse_Click(object sender, RoutedEventArgs e)
    {
        BrowseLocalTeklaFolder(
            "Выберите локальную папку Grasshopper Libraries",
            TeklaLibrariesLocalPathTextBox,
            path => _settings.TeklaLibrariesLocalPath = path,
            "Libraries");
    }

    private void BrowseLocalTeklaFolder(
        string description,
        System.Windows.Controls.TextBox targetTextBox,
        Action<string> applyPath,
        string label)
    {
        try
        {
            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = description,
                ShowNewFolderButton = true,
                SelectedPath = (targetTextBox.Text ?? string.Empty).Trim()
            };

            if (dialog.ShowDialog() == Forms.DialogResult.OK)
            {
                targetTextBox.Text = dialog.SelectedPath;
                applyPath(dialog.SelectedPath);
                _settingsService.Save(_settings);
                UpdateTeklaUi();
            }
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка выбора папки " + label + ": " + ex.Message);
            ThemedDialogs.Show(this, ex.Message, "Стандарт Tekla", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenTeklaFolder(string folderPath, string label)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = folderPath,
                UseShellExecute = true
            });
            AppendLog("Открыта папка " + label + ": " + folderPath);
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка открытия папки " + label + ": " + ex.Message);
            ThemedDialogs.Show(this, ex.Message, "Стандарт Tekla", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NormalizeTeklaSettings()
    {
        _settings.TeklaStandardManifestUrl = string.IsNullOrWhiteSpace(_settings.TeklaStandardManifestUrl)
            ? FixedTeklaStandardManifestUrl
            : _settings.TeklaStandardManifestUrl;
        _settings.TeklaStandardLocalPath = string.IsNullOrWhiteSpace(_settings.TeklaStandardLocalPath)
            ? DefaultTeklaStandardLocalPath
            : _settings.TeklaStandardLocalPath;
        _settings.TeklaExtensionsManifestUrl = string.IsNullOrWhiteSpace(_settings.TeklaExtensionsManifestUrl)
            ? FixedTeklaExtensionsManifestUrl
            : _settings.TeklaExtensionsManifestUrl;
        _settings.TeklaExtensionsLocalPath = string.IsNullOrWhiteSpace(_settings.TeklaExtensionsLocalPath)
            ? DefaultTeklaExtensionsLocalPath
            : _settings.TeklaExtensionsLocalPath;
        _settings.TeklaLibrariesManifestUrl = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesManifestUrl)
            ? FixedTeklaLibrariesManifestUrl
            : _settings.TeklaLibrariesManifestUrl;
        _settings.TeklaLibrariesLocalPath = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesLocalPath)
            ? DefaultTeklaLibrariesLocalPath
            : _settings.TeklaLibrariesLocalPath;
        _settings.TeklaPublishSourcePath =
            string.IsNullOrWhiteSpace(_settings.TeklaPublishSourcePath) ||
            string.Equals(_settings.TeklaPublishSourcePath, @"\\62.113.36.107\BIM_Models\Tekla\XS_FIRM", StringComparison.OrdinalIgnoreCase)
            ? DefaultTeklaPublishSourcePath
            : _settings.TeklaPublishSourcePath;
        _settings.TeklaExtensionsPublishSourcePath =
            string.IsNullOrWhiteSpace(_settings.TeklaExtensionsPublishSourcePath) ||
            string.Equals(_settings.TeklaExtensionsPublishSourcePath, @"\\62.113.36.107\BIM_Models\Tekla\Extension", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_settings.TeklaExtensionsPublishSourcePath, @"\\62.113.36.107\BIM_Models\Tekla\Extensions", StringComparison.OrdinalIgnoreCase)
            ? DefaultTeklaExtensionsPublishSourcePath
            : _settings.TeklaExtensionsPublishSourcePath;
        _settings.TeklaLibrariesPublishSourcePath = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesPublishSourcePath)
            ? DefaultTeklaLibrariesPublishSourcePath
            : _settings.TeklaLibrariesPublishSourcePath;
    }

    private void ApplyAndPersistTeklaPathsOnly()
    {
        var firmPath = (TeklaFirmLocalPathTextBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(firmPath))
        {
            _settings.TeklaStandardLocalPath = firmPath;
        }

        var extensionsPath = (TeklaExtensionsLocalPathTextBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(extensionsPath))
        {
            _settings.TeklaExtensionsLocalPath = extensionsPath;
        }

        var librariesPath = (TeklaLibrariesLocalPathTextBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(librariesPath))
        {
            _settings.TeklaLibrariesLocalPath = librariesPath;
        }

        var firmPublishSourcePath = (TeklaPublishFirmSourcePathTextBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(firmPublishSourcePath))
        {
            _settings.TeklaPublishSourcePath = firmPublishSourcePath;
        }

        var extensionsPublishSourcePath = (TeklaPublishExtensionsSourcePathTextBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(extensionsPublishSourcePath))
        {
            _settings.TeklaExtensionsPublishSourcePath = extensionsPublishSourcePath;
        }

        var librariesPublishSourcePath = (TeklaPublishLibrariesSourcePathTextBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(librariesPublishSourcePath))
        {
            _settings.TeklaLibrariesPublishSourcePath = librariesPublishSourcePath;
        }
    }

    private TeklaManagedTargetState CreateFirmTargetState()
    {
        return new TeklaManagedTargetState
        {
            Key = "firm",
            DisplayName = "Папка фирмы Tekla",
            ManifestUrl = _settings.TeklaStandardManifestUrl,
            LocalPath = _settings.TeklaStandardLocalPath,
            InstalledVersion = _settings.TeklaStandardInstalledVersion,
            TargetVersion = _settings.TeklaStandardTargetVersion,
            InstalledRevision = _settings.TeklaStandardInstalledRevision,
            TargetRevision = _settings.TeklaStandardTargetRevision,
            LastCheckUtc = _settings.TeklaStandardLastCheckUtc,
            LastSuccessUtc = _settings.TeklaStandardLastSuccessUtc,
            PendingAfterClose = _settings.TeklaStandardPendingAfterClose,
            LastError = _settings.TeklaStandardLastError,
            RepoUrl = _settings.TeklaStandardRepoUrl,
            RepoRef = _settings.TeklaStandardRepoRef,
            RepoSubdir = _settings.TeklaStandardRepoSubdir,
            SyncMode = TeklaManagedSyncMode.Strict,
            DelayWhenTeklaRunning = true
        };
    }

    private void ApplyFirmTargetState(TeklaManagedTargetState target)
    {
        _settings.TeklaStandardManifestUrl = target.ManifestUrl;
        _settings.TeklaStandardLocalPath = target.LocalPath;
        _settings.TeklaStandardInstalledVersion = target.InstalledVersion;
        _settings.TeklaStandardTargetVersion = target.TargetVersion;
        _settings.TeklaStandardInstalledRevision = target.InstalledRevision;
        _settings.TeklaStandardTargetRevision = target.TargetRevision;
        _settings.TeklaStandardLastCheckUtc = target.LastCheckUtc;
        _settings.TeklaStandardLastSuccessUtc = target.LastSuccessUtc;
        _settings.TeklaStandardPendingAfterClose = target.PendingAfterClose;
        _settings.TeklaStandardLastError = target.LastError;
        _settings.TeklaStandardRepoUrl = target.RepoUrl;
        _settings.TeklaStandardRepoRef = target.RepoRef;
        _settings.TeklaStandardRepoSubdir = target.RepoSubdir;
        TeklaStatusTextBlock.Text = BuildFirmStatusText();
    }

    private TeklaManagedTargetState CreateExtensionsTargetState()
    {
        return new TeklaManagedTargetState
        {
            Key = "extensions",
            DisplayName = "Extensions Tekla",
            ManifestUrl = _settings.TeklaExtensionsManifestUrl,
            LocalPath = _settings.TeklaExtensionsLocalPath,
            InstalledVersion = _settings.TeklaExtensionsInstalledVersion,
            TargetVersion = _settings.TeklaExtensionsTargetVersion,
            InstalledRevision = _settings.TeklaExtensionsInstalledRevision,
            TargetRevision = _settings.TeklaExtensionsTargetRevision,
            LastCheckUtc = _settings.TeklaExtensionsLastCheckUtc,
            LastSuccessUtc = _settings.TeklaExtensionsLastSuccessUtc,
            PendingAfterClose = _settings.TeklaExtensionsPendingAfterClose,
            LastError = _settings.TeklaExtensionsLastError,
            RepoUrl = _settings.TeklaExtensionsRepoUrl,
            RepoRef = _settings.TeklaExtensionsRepoRef,
            RepoSubdir = _settings.TeklaExtensionsRepoSubdir,
            SyncMode = TeklaManagedSyncMode.OverlayNoDelete,
            DelayWhenTeklaRunning = true
        };
    }

    private void ApplyExtensionsTargetState(TeklaManagedTargetState target)
    {
        _settings.TeklaExtensionsManifestUrl = target.ManifestUrl;
        _settings.TeklaExtensionsLocalPath = target.LocalPath;
        _settings.TeklaExtensionsInstalledVersion = target.InstalledVersion;
        _settings.TeklaExtensionsTargetVersion = target.TargetVersion;
        _settings.TeklaExtensionsInstalledRevision = target.InstalledRevision;
        _settings.TeklaExtensionsTargetRevision = target.TargetRevision;
        _settings.TeklaExtensionsLastCheckUtc = target.LastCheckUtc;
        _settings.TeklaExtensionsLastSuccessUtc = target.LastSuccessUtc;
        _settings.TeklaExtensionsPendingAfterClose = target.PendingAfterClose;
        _settings.TeklaExtensionsLastError = target.LastError;
        _settings.TeklaExtensionsRepoUrl = target.RepoUrl;
        _settings.TeklaExtensionsRepoRef = target.RepoRef;
        _settings.TeklaExtensionsRepoSubdir = target.RepoSubdir;
        TeklaExtensionsStatusTextBlock.Text = BuildExtensionsStatusText();
    }

    private TeklaManagedTargetState CreateLibrariesTargetState()
    {
        return new TeklaManagedTargetState
        {
            Key = "libraries",
            DisplayName = "Grasshopper Libraries",
            ManifestUrl = _settings.TeklaLibrariesManifestUrl,
            LocalPath = _settings.TeklaLibrariesLocalPath,
            InstalledVersion = _settings.TeklaLibrariesInstalledVersion,
            TargetVersion = _settings.TeklaLibrariesTargetVersion,
            InstalledRevision = _settings.TeklaLibrariesInstalledRevision,
            TargetRevision = _settings.TeklaLibrariesTargetRevision,
            LastCheckUtc = _settings.TeklaLibrariesLastCheckUtc,
            LastSuccessUtc = _settings.TeklaLibrariesLastSuccessUtc,
            PendingAfterClose = _settings.TeklaLibrariesPendingAfterClose,
            LastError = _settings.TeklaLibrariesLastError,
            RepoUrl = _settings.TeklaLibrariesRepoUrl,
            RepoRef = _settings.TeklaLibrariesRepoRef,
            RepoSubdir = _settings.TeklaLibrariesRepoSubdir,
            SyncMode = TeklaManagedSyncMode.OverlayNoDelete,
            DelayWhenTeklaRunning = false
        };
    }

    private void ApplyLibrariesTargetState(TeklaManagedTargetState target)
    {
        _settings.TeklaLibrariesManifestUrl = target.ManifestUrl;
        _settings.TeklaLibrariesLocalPath = target.LocalPath;
        _settings.TeklaLibrariesInstalledVersion = target.InstalledVersion;
        _settings.TeklaLibrariesTargetVersion = target.TargetVersion;
        _settings.TeklaLibrariesInstalledRevision = target.InstalledRevision;
        _settings.TeklaLibrariesTargetRevision = target.TargetRevision;
        _settings.TeklaLibrariesLastCheckUtc = target.LastCheckUtc;
        _settings.TeklaLibrariesLastSuccessUtc = target.LastSuccessUtc;
        _settings.TeklaLibrariesPendingAfterClose = target.PendingAfterClose;
        _settings.TeklaLibrariesLastError = target.LastError;
        _settings.TeklaLibrariesRepoUrl = target.RepoUrl;
        _settings.TeklaLibrariesRepoRef = target.RepoRef;
        _settings.TeklaLibrariesRepoSubdir = target.RepoSubdir;
        TeklaLibrariesStatusTextBlock.Text = BuildLibrariesStatusText();
    }

    private string BuildFirmStatusText()
    {
        if (_settings.TeklaStandardPendingAfterClose)
        {
            return "Папка фирмы: обновление подготовлено и будет применено после закрытия Tekla";
        }

        if (string.Equals(_settings.TeklaStandardLastError, "manifest_not_received", StringComparison.OrdinalIgnoreCase))
        {
            return "Папка фирмы: не удалось получить данные обновления с сервера";
        }

        if (!string.IsNullOrWhiteSpace(_settings.TeklaStandardLastError))
        {
            return _settings.TeklaStandardLastError;
        }

        if (_teklaStandardService.IsUpdateAvailable(_settings.TeklaStandardInstalledRevision, _settings.TeklaStandardTargetRevision))
        {
            var currentRevision = string.IsNullOrWhiteSpace(_settings.TeklaStandardInstalledRevision)
                ? "не установлена"
                : _settings.TeklaStandardInstalledRevision;
            return "Папка фирмы: требуется обновление до ревизии " + _settings.TeklaStandardTargetRevision + " (сейчас " + currentRevision + ")";
        }

        if (!string.IsNullOrWhiteSpace(_settings.TeklaStandardInstalledRevision))
        {
            return "Папка фирмы: актуально (" + _settings.TeklaStandardInstalledRevision + ")";
        }

        return "Папка фирмы: проверка еще не выполнялась";
    }

    private string BuildExtensionsStatusText()
    {
        if (_settings.TeklaExtensionsPendingAfterClose)
        {
            return "Пользовательские приложения: обновление подготовлено и будет применено после закрытия Tekla";
        }

        if (string.Equals(_settings.TeklaExtensionsLastError, "manifest_not_received", StringComparison.OrdinalIgnoreCase))
        {
            return "Пользовательские приложения: не удалось получить данные обновления с сервера";
        }

        if (!string.IsNullOrWhiteSpace(_settings.TeklaExtensionsLastError))
        {
            return _settings.TeklaExtensionsLastError;
        }

        if (_teklaStandardService.IsUpdateAvailable(_settings.TeklaExtensionsInstalledRevision, _settings.TeklaExtensionsTargetRevision))
        {
            var currentRevision = string.IsNullOrWhiteSpace(_settings.TeklaExtensionsInstalledRevision)
                ? "не установлена"
                : _settings.TeklaExtensionsInstalledRevision;
            return "Пользовательские приложения: требуется обновление до ревизии " + _settings.TeklaExtensionsTargetRevision + " (сейчас " + currentRevision + ")";
        }

        if (!string.IsNullOrWhiteSpace(_settings.TeklaExtensionsInstalledRevision))
        {
            return "Пользовательские приложения: актуально (" + _settings.TeklaExtensionsInstalledRevision + ")";
        }

        return "Пользовательские приложения: проверка еще не выполнялась";
    }

    private string BuildLibrariesStatusText()
    {
        if (_settings.TeklaLibrariesPendingAfterClose)
        {
            return "Grasshopper Libraries: обновление подготовлено и будет применено автоматически";
        }

        if (string.Equals(_settings.TeklaLibrariesLastError, "manifest_not_received", StringComparison.OrdinalIgnoreCase))
        {
            return "Grasshopper Libraries: не удалось получить данные обновления с сервера";
        }

        if (!string.IsNullOrWhiteSpace(_settings.TeklaLibrariesLastError))
        {
            return _settings.TeklaLibrariesLastError;
        }

        if (_teklaStandardService.IsUpdateAvailable(_settings.TeklaLibrariesInstalledRevision, _settings.TeklaLibrariesTargetRevision))
        {
            var currentRevision = string.IsNullOrWhiteSpace(_settings.TeklaLibrariesInstalledRevision)
                ? "не установлена"
                : _settings.TeklaLibrariesInstalledRevision;
            return "Grasshopper Libraries: требуется обновление до ревизии " + _settings.TeklaLibrariesTargetRevision + " (сейчас " + currentRevision + ")";
        }

        if (!string.IsNullOrWhiteSpace(_settings.TeklaLibrariesInstalledRevision))
        {
            return "Grasshopper Libraries: актуально (" + _settings.TeklaLibrariesInstalledRevision + ")";
        }

        return "Grasshopper Libraries: проверка еще не выполнялась";
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

    private sealed class TeklaPublishTargetSelection
    {
        public string TargetKey { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string SourcePath { get; init; } = "";
    }

    private List<TeklaPublishTargetSelection> GetSelectedPublishTargets()
    {
        var targets = new List<TeklaPublishTargetSelection>();

        if (PublishFirmCheckBox.IsChecked == true)
        {
            targets.Add(new TeklaPublishTargetSelection
            {
                TargetKey = "firm",
                DisplayName = "Папка фирмы",
                SourcePath = (TeklaPublishFirmSourcePathTextBox.Text ?? string.Empty).Trim()
            });
        }

        if (PublishExtensionsCheckBox.IsChecked == true)
        {
            targets.Add(new TeklaPublishTargetSelection
            {
                TargetKey = "extensions",
                DisplayName = "Пользовательские приложения",
                SourcePath = (TeklaPublishExtensionsSourcePathTextBox.Text ?? string.Empty).Trim()
            });
        }

        if (PublishLibrariesCheckBox.IsChecked == true)
        {
            targets.Add(new TeklaPublishTargetSelection
            {
                TargetKey = "libraries",
                DisplayName = "Grasshopper Libraries",
                SourcePath = (TeklaPublishLibrariesSourcePathTextBox.Text ?? string.Empty).Trim()
            });
        }

        return targets;
    }

    private void TeklaPublishFirmBrowse_Click(object sender, RoutedEventArgs e)
    {
        BrowsePublishSourceFolder("Папка фирмы", TeklaPublishFirmSourcePathTextBox, DefaultTeklaPublishSourcePath, path => _settings.TeklaPublishSourcePath = path);
    }

    private void TeklaPublishExtensionsBrowse_Click(object sender, RoutedEventArgs e)
    {
        BrowsePublishSourceFolder("Пользовательские приложения", TeklaPublishExtensionsSourcePathTextBox, DefaultTeklaExtensionsPublishSourcePath, path => _settings.TeklaExtensionsPublishSourcePath = path);
    }

    private void TeklaPublishLibrariesBrowse_Click(object sender, RoutedEventArgs e)
    {
        BrowsePublishSourceFolder("Grasshopper Libraries", TeklaPublishLibrariesSourcePathTextBox, DefaultTeklaLibrariesPublishSourcePath, path => _settings.TeklaLibrariesPublishSourcePath = path);
    }

    private void BrowsePublishSourceFolder(
        string label,
        System.Windows.Controls.TextBox targetTextBox,
        string fallbackPath,
        Action<string> saveToSettings)
    {
        try
        {
            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = "Выберите эталонную серверную папку для раздела: " + label,
                ShowNewFolderButton = false,
                SelectedPath = string.IsNullOrWhiteSpace(targetTextBox.Text)
                    ? fallbackPath
                    : targetTextBox.Text.Trim()
            };

            if (dialog.ShowDialog() != Forms.DialogResult.OK)
            {
                return;
            }

            targetTextBox.Text = dialog.SelectedPath;
            saveToSettings(dialog.SelectedPath);
            _settingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка выбора эталонной папки для раздела " + label + ": " + ex.Message);
            ThemedDialogs.Show(this, ex.Message, "Стандарт Tekla", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TeklaOpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(_teklaStandardService.LogFilePath))
            {
                File.WriteAllText(_teklaStandardService.LogFilePath, string.Empty);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + _teklaStandardService.LogFilePath + "\"",
                UseShellExecute = true
            });
            AppendLog("Открыт лог Стандарт Tekla: " + _teklaStandardService.LogFilePath);
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка открытия лога Стандарт Tekla: " + ex.Message);
            ThemedDialogs.Show(this, ex.Message, "Стандарт Tekla", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TeklaPublish_Click(object sender, RoutedEventArgs e)
    {
        OperationProgressWindow? progressWindow = null;
        try
        {
            if (!_settings.IsFirmAdmin)
            {
                throw new InvalidOperationException("Публикация доступна только для роли admin_firm.");
            }

            var token = SettingsService.DecryptToken(_settings.TokenCipherBase64).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Токен устройства не найден. Выполните подключение по токену.");
            }

            var selectedTargets = GetSelectedPublishTargets();
            if (selectedTargets.Count == 0)
            {
                throw new InvalidOperationException("Выберите хотя бы один раздел для публикации.");
            }

            var publishComment = (TeklaPublishNotesTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(publishComment))
            {
                throw new InvalidOperationException("Комментарий публикации обязателен.");
            }

            foreach (var selectedTarget in selectedTargets)
            {
                if (string.IsNullOrWhiteSpace(selectedTarget.SourcePath))
                {
                    throw new InvalidOperationException("Для раздела \"" + selectedTarget.DisplayName + "\" не указан путь к эталонной папке.");
                }
            }

            _settings.TeklaPublishSourcePath = (TeklaPublishFirmSourcePathTextBox.Text ?? string.Empty).Trim();
            _settings.TeklaExtensionsPublishSourcePath = (TeklaPublishExtensionsSourcePathTextBox.Text ?? string.Empty).Trim();
            _settings.TeklaLibrariesPublishSourcePath = (TeklaPublishLibrariesSourcePathTextBox.Text ?? string.Empty).Trim();
            _settingsService.Save(_settings);

            var totalSteps = selectedTargets.Count + 3;
            var currentStep = 0;
            progressWindow = new OperationProgressWindow("Публикация Tekla", "Подготавливаем публикацию");
            progressWindow.Owner = this;
            progressWindow.Show();

            TeklaPublishButton.IsEnabled = false;

            var resultLines = new List<string>();
            foreach (var selectedTarget in selectedTargets)
            {
                currentStep++;
                progressWindow.UpdateStep(
                    "Публикуем раздел: " + selectedTarget.DisplayName,
                    "Идет проверка изменений и подготовка публикации",
                    currentStep,
                    totalSteps,
                    EstimateOperationEta(currentStep, totalSteps));

                var payload = new TeklaManifestPublishPayload
                {
                    Target = selectedTarget.TargetKey,
                    SourcePath = selectedTarget.SourcePath,
                    Comment = publishComment
                };
                var result = await _heartbeatClient.PublishTeklaManifestAsync(_settings.ServerUrl, token, payload, CancellationToken.None);

                if (result.NoChanges)
                {
                    resultLines.Add(selectedTarget.DisplayName + ": изменений не найдено");
                    AppendLog("Публикация " + selectedTarget.TargetKey + ": изменений не обнаружено.");
                }
                else
                {
                    resultLines.Add(selectedTarget.DisplayName + ": опубликовано, ревизия " + result.Revision);
                    AppendLog("Публикация " + selectedTarget.TargetKey + ": опубликовано, ревизия " + result.Revision);
                }

                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    AppendLog(result.Message);
                }
            }

            currentStep++;
            progressWindow.UpdateStep(
                "Обновляем данные по состоянию",
                "Сохраняем информацию о публикации",
                currentStep,
                totalSteps,
                EstimateOperationEta(currentStep, totalSteps));
            _settingsService.Save(_settings);

            currentStep++;
            progressWindow.UpdateStep(
                "Запускаем синхронизацию на этом компьютере",
                "Сейчас локальные папки будут приведены к опубликованному состоянию",
                currentStep,
                totalSteps,
                EstimateOperationEta(currentStep, totalSteps));
            await RunTeklaSyncCycleAsync(
                showDialogs: false,
                forceRefresh: true,
                autoApplyIfPossible: true);

            currentStep++;
            progressWindow.UpdateStep(
                "Готово",
                "Публикация и синхронизация завершены",
                currentStep,
                totalSteps,
                TimeSpan.Zero);
            progressWindow.MarkSucceeded(string.Join(Environment.NewLine, resultLines));

            ThemedDialogs.Show(
                this,
                "Публикация завершена\n\n" + string.Join(Environment.NewLine, resultLines),
                "Стандарт Tekla",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            var message = GetFriendlyTeklaPublishErrorMessage(ex);
            progressWindow?.MarkFailed(message);
            AppendLog("Ошибка публикации Tekla: " + message);
            ThemedDialogs.Show(this, message, "Стандарт Tekla", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TeklaPublishButton.IsEnabled = _settings.IsFirmAdmin;
        }
    }

    private static TimeSpan EstimateOperationEta(int currentStep, int totalSteps)
    {
        var remainingSteps = Math.Max(0, totalSteps - currentStep);
        return TimeSpan.FromSeconds(Math.Max(0, remainingSteps * 12));
    }

    private void TeklaPublishBrowse_Click(object sender, RoutedEventArgs e)
    {
        // Legacy hidden control left for backward XAML compatibility.
        TeklaPublishFirmBrowse_Click(sender, e);
    }

    private void TeklaPublishTarget_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Legacy hidden control left for backward XAML compatibility.
    }

    private static string GetFriendlyTeklaPublishErrorMessage(Exception ex)
    {
        var message = ex.Message?.Trim() ?? "Неизвестная ошибка публикации.";

        if (message.StartsWith("HTTP 409:", StringComparison.OrdinalIgnoreCase))
        {
            return "На сервере уже выполняется публикация. Дождитесь завершения текущей попытки и повторите запуск позже";
        }

        if (message.StartsWith("HTTP 504:", StringComparison.OrdinalIgnoreCase))
        {
            return message[9..].Trim();
        }

        if (message.StartsWith("HTTP 400:", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("HTTP 500:", StringComparison.OrdinalIgnoreCase))
        {
            return message[9..].Trim();
        }

        return message;
    }

    private async void RestartTeklaServer_Click(object sender, RoutedEventArgs e)
    {
        await RestartManagedServerAsync("tekla", RestartTeklaServerButton, "Tekla Server");
    }

    private async Task RestartManagedServerAsync(string serviceKey, System.Windows.Controls.Button button, string displayName)
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
            RestartTeklaServerButton.IsEnabled = _settings.IsSystemAdmin || _settings.IsFirmAdmin;
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

    private static void ConnectShareWithAnyLogin(string shareRoot, string password, IEnumerable<string> loginCandidates)
    {
        Exception? last = null;
        foreach (var candidate in loginCandidates)
        {
            try
            {
                RunProcessOrThrow("net", "use", shareRoot, password, $"/user:{candidate}", "/persistent:no");
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

    private static void DisconnectAllSmbSessions()
    {
        try
        {
            RunProcessOrThrow("net", "use", "*", "/delete", "/y");
        }
        catch (InvalidOperationException ex) when (IsWindowsNetNoEntries(ex.Message) || IsWindowsNetConnectionNotFound(ex.Message))
        {
            // No active SMB sessions in current user profile.
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
    }

    private static void DeleteStoredWindowsCredentialForHost(string host)
    {
        var targets = new[]
        {
            host,
            $"TERMSRV/{host}",
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

    private async Task ConnectSmbInternalAsync(string login, string password, string sharePath, bool openExplorer)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Введите SMB логин и пароль.");
        }

        var host = GetSmbHost(sharePath);
        var shareRoot = GetSmbShareRoot(sharePath);
        var loginCandidates = BuildSmbLoginCandidates(login, host);
        SmbLoginTextBox.Text = loginCandidates.FirstOrDefault() ?? login;

        await Task.Run(() =>
        {
            DeleteStoredWindowsCredentialForHost(host);

            try
            {
                RunProcessOrThrow("net", "use", shareRoot, "/delete", "/y");
            }
            catch
            {
                // Ignore cleanup errors for non-existing mappings.
            }

            try
            {
                ConnectShareWithAnyLogin(shareRoot, password, loginCandidates);
            }
            catch (InvalidOperationException ex) when (IsWindowsSmbConflict(ex.Message))
            {
                DisconnectAllSmbSessionsForHost(host);
                try
                {
                    ConnectShareWithAnyLogin(shareRoot, password, loginCandidates);
                }
                catch (InvalidOperationException retryEx) when (IsWindowsSmbConflict(retryEx.Message))
                {
                    DisconnectAllSmbSessions();
                    try
                    {
                        RunProcessOrThrow("net", "use", $@"\\{host}\IPC$", "/delete", "/y");
                    }
                    catch (InvalidOperationException ipcEx) when (IsWindowsNetConnectionNotFound(ipcEx.Message) || IsWindowsNetNoEntries(ipcEx.Message))
                    {
                        // IPC session not present.
                    }
                    ConnectShareWithAnyLogin(shareRoot, password, loginCandidates);
                }
            }
        });

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

            var login = SmbLoginTextBox.Text.Trim();
            var password = SmbPasswordBox.Password.Trim();
            var sharePath = SmbSharePathTextBox.Text.Trim();
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
            var sharePath = SmbSharePathTextBox.Text.Trim();
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
