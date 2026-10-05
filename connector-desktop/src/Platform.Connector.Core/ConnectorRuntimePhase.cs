namespace Platform.Connector.Core;

/// <summary>
/// Жизненный цикл runtime коннектора, видимый пользователю.
/// Переходы (управляет <see cref="ConnectorRuntimeSupervisor"/>):
///
///   Stopped ──Start──► Connecting ──bootstrap ok──► Connected
///   Connecting ──сервер недоступен──► WaitingForServer ──retry──► Connecting
///   Connected ──409 session conflict / сбой worker──► Reconnecting ──bootstrap ok──► Connected
///   Connecting/Reconnecting/Connected ──401/403──► TokenInvalid (терминально, до ручного действия)
///   любой ──Stop──► Stopped
/// </summary>
public enum ConnectorRuntimePhase
{
    /// <summary>Runtime не запущен (до старта или после остановки пользователем).</summary>
    Stopped,

    /// <summary>Первый bootstrap после запуска.</summary>
    Connecting,

    /// <summary>Сервер недоступен на старте — повторяем bootstrap с backoff.</summary>
    WaitingForServer,

    /// <summary>Bootstrap успешен, worker обрабатывает heartbeat/poll/jobs.</summary>
    Connected,

    /// <summary>Сессия вытеснена или runtime упал — авто-re-bootstrap с backoff.</summary>
    Reconnecting,

    /// <summary>Сервер отклонил токен (401/403). Ретраев нет — нужен новый токен.</summary>
    TokenInvalid
}

/// <summary>Грубая «окраска» фазы для бейджей/иконок UI.</summary>
public enum ConnectorPhaseSeverity
{
    /// <summary>Серый: ничего не происходит.</summary>
    Idle,

    /// <summary>Жёлтый: переходное состояние, идёт работа по подключению.</summary>
    Busy,

    /// <summary>Зелёный: всё хорошо.</summary>
    Ok,

    /// <summary>Красный: требуется действие пользователя.</summary>
    Error
}

/// <summary>
/// Презентация фаз для UI (русские подписи + severity). Вынесена в Core,
/// чтобы маппинг был покрыт unit-тестами без WPF.
/// </summary>
public static class ConnectorPhasePresentation
{
    public static string GetRussianLabel(ConnectorRuntimePhase phase) => phase switch
    {
        ConnectorRuntimePhase.Stopped => "Остановлен",
        ConnectorRuntimePhase.Connecting => "Подключение…",
        ConnectorRuntimePhase.WaitingForServer => "Ожидание сервера…",
        ConnectorRuntimePhase.Connected => "Подключён",
        ConnectorRuntimePhase.Reconnecting => "Переподключение…",
        ConnectorRuntimePhase.TokenInvalid => "Токен недействителен",
        _ => phase.ToString()
    };

    public static ConnectorPhaseSeverity GetSeverity(ConnectorRuntimePhase phase) => phase switch
    {
        ConnectorRuntimePhase.Stopped => ConnectorPhaseSeverity.Idle,
        ConnectorRuntimePhase.Connecting => ConnectorPhaseSeverity.Busy,
        ConnectorRuntimePhase.WaitingForServer => ConnectorPhaseSeverity.Busy,
        ConnectorRuntimePhase.Connected => ConnectorPhaseSeverity.Ok,
        ConnectorRuntimePhase.Reconnecting => ConnectorPhaseSeverity.Busy,
        ConnectorRuntimePhase.TokenInvalid => ConnectorPhaseSeverity.Error,
        _ => ConnectorPhaseSeverity.Idle
    };
}
