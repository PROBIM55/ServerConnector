// Wave B: контракты session-слоя AutoCAD (подключение к ЗАПУЩЕННОМУ AutoCAD
// через COM/ROT, в отличие от legacy batch-пути «acad.exe /b script»).
//
// Всё, что здесь — pure-модели без COM-зависимостей: парсинг payload,
// резолв сессии и конвертация сущностей покрываются unit-тестами без AutoCAD.

namespace Platform.Cad.AutoCad.Adapter.Sessions;

/// <summary>
/// Снимок одной запущенной сессии AutoCAD (одна строка списка в UI коннектора).
/// </summary>
public sealed record AutoCadSessionInfo(
    string SessionKey,
    int ProcessId,
    string Version,
    string? DocumentName,
    string? DocumentPath,
    string Caption)
{
    /// <summary>Отображаемое имя для UI: «AutoCAD 24.1 — Чертёж1.dwg».</summary>
    public string DisplayName =>
        $"AutoCAD {Version} — {(string.IsNullOrWhiteSpace(DocumentName) ? "документы не открыты" : DocumentName)}";
}

/// <summary>Результат action=ping: краткая сводка по сессии.</summary>
public sealed record AutoCadPingInfo(
    string Version,
    string? DocumentName,
    string? DocumentPath,
    int DocumentsCount,
    int? ModelSpaceCount);

public enum AutoCadSessionAction
{
    Ping,
    DrawEntities,
    EraseByHandles,
    ZoomExtents
}

/// <summary>Разобранный session-запрос (payload mode=session).</summary>
public sealed record AutoCadSessionRequest(
    AutoCadSessionAction Action,
    string? SessionKey,
    IReadOnlyList<AutoCadEntitySpec> Entities,
    IReadOnlyList<string> Handles);

/// <summary>Спецификация сущности для action=drawEntities (v1: line/polyline/circle/text).</summary>
public abstract record AutoCadEntitySpec;

public sealed record AutoCadLineSpec(double X1, double Y1, double Z1, double X2, double Y2, double Z2) : AutoCadEntitySpec;

/// <summary>Точки — массивы [x,y] или [x,y,z]; рисуется через ModelSpace.AddPolyline (3D flat array).</summary>
public sealed record AutoCadPolylineSpec(IReadOnlyList<IReadOnlyList<double>> Points, bool Closed) : AutoCadEntitySpec;

public sealed record AutoCadCircleSpec(double Cx, double Cy, double Cz, double R) : AutoCadEntitySpec;

public sealed record AutoCadTextSpec(double X, double Y, double Z, double Height, string Value) : AutoCadEntitySpec;

/// <summary>
/// Итог выполнения session-операции. ErrorCode — из <see cref="AutoCadSessionErrorCodes"/>.
/// </summary>
public sealed record AutoCadSessionOperationResult(
    bool IsSuccess,
    string? ErrorCode = null,
    string? Message = null,
    AutoCadSessionInfo? Session = null,
    IReadOnlyList<string>? CreatedHandles = null,
    int? ErasedCount = null,
    IReadOnlyList<string>? NotFoundHandles = null,
    AutoCadPingInfo? Ping = null);

public static class AutoCadSessionErrorCodes
{
    public const string PayloadInvalid = "AUTOCAD_SESSION_PAYLOAD_INVALID";
    public const string NoRunningSessions = "AUTOCAD_NO_RUNNING_SESSIONS";
    public const string SessionNotFound = "AUTOCAD_SESSION_NOT_FOUND";
    public const string SessionNotSelected = "AUTOCAD_SESSION_NOT_SELECTED";
    public const string NoActiveDocument = "AUTOCAD_NO_ACTIVE_DOCUMENT";
    public const string SessionBusy = "AUTOCAD_SESSION_BUSY";
    public const string ComError = "AUTOCAD_COM_ERROR";
    public const string NotSupportedOnPlatform = "AUTOCAD_SESSION_WINDOWS_ONLY";
}

/// <summary>
/// Исполнитель session-операций. Production-реализация — <see cref="AutoCadSessionService"/>
/// (ROT + late-bound COM на выделенном STA-потоке); в тестах подменяется fake'ом,
/// чтобы проверять роутинг адаптера без AutoCAD.
/// </summary>
public interface IAutoCadSessionExecutor
{
    Task<IReadOnlyList<AutoCadSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken);

    Task<AutoCadSessionOperationResult> ExecuteAsync(AutoCadSessionRequest request, CancellationToken cancellationToken);
}
