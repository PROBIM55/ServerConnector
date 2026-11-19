using System.Windows;
using Connector.Desktop.Models;

namespace Connector.Desktop.Features.Connector;

// Only non-secret native selections cross the Graphite boundary. Empty paths
// mean "keep the saved value"; passwords and tokens never enter this DTO.
public sealed record DesktopPreferences(
    string? TeklaFirmLocalPath,
    string? TeklaExtensionsLocalPath,
    string? TeklaLibrariesLocalPath,
    string? ModelSharingTeklaBin,
    string? IfcPatchingTeklaBin,
    string? IfcPatchingStagingDir,
    bool? AutoStart,
    int? HeartbeatSeconds,
    string? ConverterOutputDirectory = null,
    string? TeklaPublishSourcePath = null,
    string? TeklaExtensionsPublishSourcePath = null,
    string? TeklaLibrariesPublishSourcePath = null,
    string? UiTheme = null);

// IPC carries only target selection and the publication comment. Native source
// paths stay in AppSettings and are resolved by the existing Tekla publisher.
public sealed record TeklaPublicationRequest(
    bool PublishFirm,
    bool PublishExtensions,
    bool PublishLibraries,
    string Comment);

// Native-only actions exposed to the Graphite boundary. Application updates are not among them:
// the machine update service downloads and installs; the application only reads its status.
public enum DesktopSupportAction
{
    ShowReleaseNotes,
    OpenApplicationJournal,
    ClearApplicationJournal,
    ExportDiagnostics,
    OpenLogFolder,
    SyncTeklaFirm,
    SyncTeklaExtensions,
    SyncTeklaLibraries
}

// The seam between the lifted "Коннектор" TAB (ConnectorView — the login/heartbeat front-end) and the shell
// (MainWindow). UNLIKE the Стандарт migration, the ENGINE STAYS in MainWindow: ConnectByTokenInternalAsync,
// SendHeartbeatSafeAsync, Timer_Tick, the heartbeat/update timers, CheckUpdatesAsync, InstallPendingUpdateAsync,
// LoadSettingsToUi/ReadSettingsFromUi/ApplyAndPersist, ConnectSmbInternalAsync — all unchanged. ONLY the tab's
// VIEW + its thin handlers moved into the module; those handlers call the engine through THIS interface.
// MainWindow implements it. Keep this surface MINIMAL — exactly what the moved handlers touch.
public interface IConnectorHost
{
    // Read only: a completed connect Task alone is not proof of a live session.
    bool IsConnected => false;
    bool CanControlBackgroundConnection => false;
    Task StartBackgroundConnectionAsync(CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Фоновое подключение недоступно."));
    Task StopBackgroundConnectionAsync(CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Приостановка подключения недоступна."));
    Task DisconnectAsync() => Task.FromException(new NotSupportedException("Отключение недоступно."));
    Task SavePreferencesAsync(bool autoStart, int heartbeatSeconds) =>
        Task.FromException(new NotSupportedException("Сохранение настроек недоступно."));
    Task SaveDesktopPreferencesAsync(DesktopPreferences preferences) =>
        Task.FromException(new NotSupportedException("Сохранение native-настроек недоступно."));
    bool CanPublishTekla => false;
    Task ValidateTeklaPublicationAsync(TeklaPublicationRequest request, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Проверка публикации Tekla недоступна."));
    Task PublishTeklaAsync(TeklaPublicationRequest request, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Публикация Tekla недоступна."));
    Task EnsureVpnReadyAsync() =>
        Task.FromException(new NotSupportedException("Подготовка VPN недоступна."));
    Task MountVpnShareAsync(string drive) =>
        Task.FromException(new NotSupportedException("Подключение общей папки недоступно."));
    Task UnmountVpnShareAsync(string drive) =>
        Task.FromException(new NotSupportedException("Отключение общей папки недоступно."));
    Task ExecuteSupportActionAsync(DesktopSupportAction action, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException($"Native-действие {action} недоступно."));
    // ===== Engine entry points the moved handlers invoke (the engine itself is unchanged) =================

    // The login/heartbeat SPINE. ConnectByToken_Click calls this. Satisfied by: MainWindow's
    // ConnectByTokenInternalAsync(token, showSuccessDialog) — the SACRED engine, called VERBATIM.
    Task ConnectByTokenAsync(string token, bool showSuccessDialog);

    // «Проверить обновления»: sets the «проверь сейчас» flag for the machine update service (at most once a
    // minute is honoured there). Nothing is downloaded or installed by the application.
    Task RequestUpdateCheckAsync();

    // The service knows a newer version (available, downloading, installing or retrying).
    bool HasPendingUpdate { get; }

    // What the page shows about updates, from the service status file.
    Services.DesktopUpdateState UpdateState => Services.DesktopUpdateState.Unknown;

    // The automatic update completed before this start, shown once («Коннектор обновлён до X» + notes).
    global::Connector.Updates.CompletedUpdate? JustUpdated => null;

    // The Tekla-mirror sync button (ConnectorTeklaSyncButton). Satisfied by: MainWindow's
    // `_standard?.RunInteractiveSyncAsync()` (the existing Стандарт interactive sync, identical to the Стандарт-tab button).
    Task RunTeklaInteractiveSyncAsync();

    // ===== Primitives the moved handlers' try/catch blocks use ===========================================

    // Journal line. Satisfied by: MainWindow `AppendLog(string)`.
    void Log(string message);

    // Window-owned themed dialog. Satisfied by: MainWindow `ThemedDialogs.Show(this, message, title, buttons, image)`.
    MessageBoxResult ShowDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image);

    // Owner window for ReleaseNotesWindow (ShowReleaseNotes_Click sets `Owner = host.OwnerWindow`).
    // Satisfied by: `this` (the MainWindow instance).
    Window OwnerWindow { get; }

    // ConnectByToken_Click's catch blocks set the shell's server-failure flag then redraw the header. These two
    // members let the moved handler reproduce that EXACTLY without owning the flag/header (which stay in the shell).
    // Satisfied by: MainWindow `_serverConnectionFailed = value` and `UpdateHeaderStatusUi()` respectively.
    void SetServerConnectionFailed(bool failed);
    void UpdateHeaderStatus();

    // The shared, mutable settings model — used ONLY for the round-trip of the COLLAPSED vestigial controls
    // (DeviceId/Interval/AutoStart/SmbSharePath) so the view can render them from the live
    // settings instead of from shell-pushed strings. LIVE getter — MainWindow reassigns _settings, never cache.
    // Satisfied by: MainWindow's `_settings` field.
    AppSettings Settings { get; }

    // Release-note catalog shown by ShowReleaseNotes_Click. Satisfied by: MainWindow's static ReleaseNotes list
    // (kept in the shell because the engine's update flow also references it).
    IReadOnlyList<ReleaseNoteItem> ReleaseNotes { get; }
}
