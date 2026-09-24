namespace Connector.AgrConversion;

/// <summary>
/// Ошибка чтения пакета или части АГР. <see cref="Exception.Message"/> — текст для пользователя: по-русски, с именем
/// архива или файла, без сырого текста исходного исключения. Сырой текст (тип и сообщение исходного исключения, строка
/// ошибки assimp) — только в <see cref="Diagnostic"/> и <see cref="Exception.InnerException"/>: для отчёта
/// диагностики, не для окна пользователя. Наследует <see cref="Exception"/>: <see cref="InvalidDataException"/>
/// запечатан, а от <see cref="IOException"/> наследовать нельзя — его ловят фильтры «файл не открылся».
/// </summary>
public sealed class AgrReadException : Exception
{
    public AgrReadException(string file, string message, Exception? inner = null, string? diagnostic = null)
        : base(message, inner)
    {
        File = file;
        Diagnostic = diagnostic ?? (inner == null ? "" : $"{inner.GetType().FullName}: {inner.Message}");
    }

    /// <summary>Имя архива или файла, о котором сообщение.</summary>
    public string File { get; }

    /// <summary>Сырой текст для отчёта диагностики.</summary>
    public string Diagnostic { get; }

    /// <summary>Файл занят другим процессом: ERROR_SHARING_VIOLATION или ERROR_LOCK_VIOLATION (по коду, не по тексту).</summary>
    public static bool IsLocked(Exception ex) => ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>Файл не открылся: не найден, занят, нет доступа или сбой ввода-вывода. <paramref name="what"/> — чей файл.</summary>
    public static AgrReadException Unreadable(string what, string file, Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => new(file, $"{what}: файл {file} не найден.", ex),
        _ when IsLocked(ex) => new(file, $"{what}: файл {file} занят другим процессом — закройте программу, которая его держит, и повторите.", ex),
        UnauthorizedAccessException => new(file, $"{what}: нет доступа к файлу {file}.", ex),
        _ => new(file, $"{what}: файл {file} не читается — ошибка ввода-вывода.", ex),
    };
}
