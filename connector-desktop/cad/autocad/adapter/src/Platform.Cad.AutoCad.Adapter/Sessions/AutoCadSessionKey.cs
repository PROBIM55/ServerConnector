namespace Platform.Cad.AutoCad.Adapter.Sessions;

/// <summary>
/// Ключ сессии v1 — по PID процесса acad.exe: «acad-12345».
/// PID транзитен (меняется при перезапуске AutoCAD), поэтому stale-ключи
/// разруливает <see cref="AutoCadSessionResolver"/> (fallback на единственную
/// живую сессию). Документ+PID как более стабильная identity — кандидат v2.
/// </summary>
public static class AutoCadSessionKey
{
    public const string Prefix = "acad-";

    public static string Format(int processId) => $"{Prefix}{processId}";

    public static bool TryParseProcessId(string? sessionKey, out int processId)
    {
        processId = 0;
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            return false;
        }

        var trimmed = sessionKey.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(trimmed[Prefix.Length..], out processId) && processId > 0;
    }
}

/// <summary>
/// Seam между Desktop UI и адаптером: UI записывает сюда выбранную пользователем
/// сессию (при старте — из DesktopSettings.AutoCadSessionKey, дальше — при смене
/// выбора в ComboBox), адаптер читает при выполнении job'ов без явного sessionKey.
/// Static-поле — осознанная простота v1: один процесс, один выбранный инстанс
/// на устройство; в тестах резолв покрывается через параметры
/// <see cref="AutoCadSessionResolver.Resolve"/>, минуя глобальное состояние.
/// </summary>
public static class AutoCadSessionSelection
{
    private static volatile string? _selectedSessionKey;

    public static string? SelectedSessionKey
    {
        get => _selectedSessionKey;
        set => _selectedSessionKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
