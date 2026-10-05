namespace Platform.Cad.AutoCad.Adapter.Sessions;

/// <summary>Результат резолва сессии: либо Session, либо ErrorCode+Message.</summary>
public sealed record AutoCadSessionResolution(
    AutoCadSessionInfo? Session,
    string? ErrorCode = null,
    string? Message = null)
{
    public bool IsResolved => Session is not null;
}

/// <summary>
/// Pure-логика выбора целевой сессии из списка запущенных. Порядок:
/// 1) явный payload.sessionKey — должен существовать, иначе SESSION_NOT_FOUND;
/// 2) выбранная в UI коннектора (selectedKey);
/// 3) единственная запущенная сессия — используется автоматически
///    (в т.ч. когда selectedKey протух после перезапуска AutoCAD);
/// 4) несколько сессий без выбора — SESSION_NOT_SELECTED (геометрию в «какую-то»
///    сессию не рисуем — это чужой чертёж).
/// </summary>
public static class AutoCadSessionResolver
{
    public static AutoCadSessionResolution Resolve(
        string? requestedKey,
        string? selectedKey,
        IReadOnlyList<AutoCadSessionInfo> sessions)
    {
        if (sessions.Count == 0)
        {
            return new AutoCadSessionResolution(
                null,
                AutoCadSessionErrorCodes.NoRunningSessions,
                "No running AutoCAD sessions were found (ROT enumeration returned no AutoCAD.Application objects).");
        }

        if (!string.IsNullOrWhiteSpace(requestedKey))
        {
            var requested = FindByKey(sessions, requestedKey);
            return requested is not null
                ? new AutoCadSessionResolution(requested)
                : new AutoCadSessionResolution(
                    null,
                    AutoCadSessionErrorCodes.SessionNotFound,
                    $"Requested AutoCAD session '{requestedKey.Trim()}' is not running. " +
                    $"Running sessions: {DescribeKeys(sessions)}.");
        }

        if (!string.IsNullOrWhiteSpace(selectedKey))
        {
            var selected = FindByKey(sessions, selectedKey);
            if (selected is not null)
            {
                return new AutoCadSessionResolution(selected);
            }

            // Выбранная сессия умерла (AutoCAD перезапущен — PID сменился).
            // Если жива ровно одна — продолжаем в неё; иначе просим перевыбрать.
            if (sessions.Count == 1)
            {
                return new AutoCadSessionResolution(sessions[0]);
            }

            return new AutoCadSessionResolution(
                null,
                AutoCadSessionErrorCodes.SessionNotSelected,
                $"Previously selected AutoCAD session '{selectedKey.Trim()}' is gone and {sessions.Count} sessions are running. " +
                "Re-select the target session in Platform Connector.");
        }

        if (sessions.Count == 1)
        {
            return new AutoCadSessionResolution(sessions[0]);
        }

        return new AutoCadSessionResolution(
            null,
            AutoCadSessionErrorCodes.SessionNotSelected,
            $"{sessions.Count} AutoCAD sessions are running and none is selected. " +
            $"Select the target session in Platform Connector or pass payload.sessionKey ({DescribeKeys(sessions)}).");
    }

    private static AutoCadSessionInfo? FindByKey(IReadOnlyList<AutoCadSessionInfo> sessions, string key)
    {
        var trimmed = key.Trim();
        foreach (var session in sessions)
        {
            if (string.Equals(session.SessionKey, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return session;
            }
        }

        return null;
    }

    private static string DescribeKeys(IReadOnlyList<AutoCadSessionInfo> sessions)
        => string.Join(", ", sessions.Select(s => s.SessionKey));
}
