using System.Globalization;

namespace Connector.AgrConversion.Service;

/// <summary>Постоянные экрана конвертера.</summary>
public static class AgrConverterConstants
{
    /// <summary>
    /// «Открыть Студию»: страница проектов Студии на проде (маршрут <c>/studio</c> во фронте Структуры,
    /// <c>frontend/src/products/routes/common.tsx</c>). Хост — как у прочих адресов Коннектора, константой. Коннектор
    /// в Структуру ничего не отправляет (Р9): адрес только открывается в браузере.
    /// </summary>
    public const string StudioUrl = "https://app.structura-most.ru/studio";

    /// <summary>Префикс строк общего журнала окна.</summary>
    public const string LogPrefix = "Конвертер: ";

    public const int DefaultParallelism = 2;
    public const int MaxParallelism = 8;
}

/// <summary>Папки и имена файлов конвертера.</summary>
public static class AgrConverterPaths
{
    public const string OutputSuffix = ".glb.zip";
    public const string ReportsFolder = "Отчёты";
    public const string ReportSuffix = ".report.json";
    public const string ManifestSuffix = ".manifest.json";
    public const string RunFolderFormat = "yyyy-MM-dd_HH-mm";

    /// <summary>Корень результатов по умолчанию: <c>Документы\Structura\Конвертер</c>.</summary>
    public static string DefaultOutputRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Structura", "Конвертер");

    /// <summary>Где Коннектор хранит настройки (<c>SettingsService</c>): <c>%LOCALAPPDATA%\ConnectorAgentDesktop</c>.</summary>
    public static string DefaultStorageDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectorAgentDesktop");

    /// <summary>Временные папки строк: <c>%TEMP%\StructuraConverter\row-&lt;id&gt;</c>.</summary>
    public static string DefaultWorkRoot => Path.Combine(Path.GetTempPath(), "StructuraConverter");

    public const string RowWorkPrefix = "row-";

    public static string RunFolderName(DateTimeOffset localTime) =>
        localTime.ToString(RunFolderFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Папка запуска <c>&lt;корень&gt;\ГГГГ-ММ-ДД_ЧЧ-ММ</c>; если такая уже есть (два запуска в одну минуту) —
    /// <c>…_2</c>, <c>…_3</c>. Папка создаётся.
    /// </summary>
    public static string CreateRunFolder(string root, DateTimeOffset localTime)
    {
        string path = FreeRunFolder(root, localTime);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Первая свободная папка запуска (при занятом имени — <c>_2</c>, <c>_3</c>); на диске не создаётся.</summary>
    public static string FreeRunFolder(string root, DateTimeOffset localTime) => FreeRunFolder(root, localTime, null);

    /// <summary>
    /// <see cref="FreeRunFolder(string, DateTimeOffset)"/>, где занятым считается и путь, для которого
    /// <paramref name="taken"/> вернул true: запуск, отменённый целиком, папку на диске не создаёт, но в истории она
    /// записана (C2b: «Новая конвертация» в ту же минуту не должна получить тот же путь).
    /// </summary>
    public static string FreeRunFolder(string root, DateTimeOffset localTime, Func<string, bool>? taken)
    {
        string baseName = RunFolderName(localTime);
        string path = Path.Combine(root, baseName);
        for (int i = 2; Directory.Exists(path) || File.Exists(path) || taken?.Invoke(path) == true; i++)
        {
            path = Path.Combine(root, $"{baseName}_{i}");
        }
        return path;
    }

    /// <summary>Имя части из имени архива (как имя модели из имени файла в Студии, Р10.2) или из имени папки.</summary>
    public static string PartName(string sourcePath)
    {
        string full = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(full);
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}

/// <summary>Этапы строки: пять точек и подписи; доля этапа → общая доля строки.</summary>
public static class AgrConversionStages
{
    public static readonly IReadOnlyList<AgrConvertPhase> All =
        new[] { AgrConvertPhase.Read, AgrConvertPhase.Geometry, AgrConvertPhase.Textures, AgrConvertPhase.Levels, AgrConvertPhase.Pack };

    // Веса — по замеру 001 (run16 C1c-1-fix): из 98 с gltfpack уровней — 78 с. Остальное поделено на глаз, не измерено.
    static readonly double[] Start = { 0, 0.03, 0.08, 0.15, 0.98 };
    static readonly double[] Weight = { 0.03, 0.05, 0.07, 0.83, 0.02 };

    public static string Label(AgrConvertPhase phase) => phase switch
    {
        AgrConvertPhase.Read => "чтение",
        AgrConvertPhase.Geometry => "геометрия",
        AgrConvertPhase.Textures => "текстуры",
        AgrConvertPhase.Levels => "уровни",
        AgrConvertPhase.Pack => "упаковка",
        _ => phase.ToString(),
    };

    public static double Overall(AgrConvertPhase phase, double phaseFraction)
    {
        int i = (int)phase;
        if (i < 0 || i >= Start.Length) return 0;
        return Start[i] + Weight[i] * Math.Clamp(double.IsFinite(phaseFraction) ? phaseFraction : 0, 0, 1);
    }
}

/// <summary>Оценка «осталось» по размеру архива и пройденной доле.</summary>
public static class AgrConversionEstimate
{
    /// <summary>Секунд на мегабайт архива до первой готовой части: 001 — 98 с на ≈ 225 МБ (run16 C1c-1-fix).</summary>
    public const double DefaultSecondsPerMb = 0.45;

    public static TimeSpan Expected(long sourceBytes, double secondsPerMb)
    {
        double rate = secondsPerMb > 0 && double.IsFinite(secondsPerMb) ? secondsPerMb : DefaultSecondsPerMb;
        return TimeSpan.FromSeconds(Math.Max(1, Math.Max(0, sourceBytes) / 1e6 * rate));
    }

    /// <summary>
    /// Смесь двух оценок: по размеру (ожидаемое время − прошло) и по доле (прошло · (1 − доля) / доля). Вес доли растёт
    /// вместе с ней: в начале верим размеру, к концу — пройденной доле.
    /// </summary>
    public static TimeSpan Remaining(TimeSpan elapsed, double fraction, long sourceBytes, double secondsPerMb)
    {
        double f = Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1);
        double e = Math.Max(0, elapsed.TotalSeconds);
        double bySize = Math.Max(0, Expected(sourceBytes, secondsPerMb).TotalSeconds - e);
        if (f <= 0 || e <= 0) return TimeSpan.FromSeconds(Math.Round(bySize));
        double byProgress = e * (1 - f) / f;
        return TimeSpan.FromSeconds(Math.Round(f * byProgress + (1 - f) * bySize));
    }
}

/// <summary>Тексты для журнала и итога.</summary>
public static class AgrConverterText
{
    public static string Plural(int n, string one, string few, string many)
    {
        int n10 = n % 10, n100 = n % 100;
        return n10 == 1 && n100 != 11 ? one : n10 is >= 2 and <= 4 && n100 is < 12 or > 14 ? few : many;
    }

    /// <summary>«Готово 15 из 16, 1 ошибка», «…, отменено 2», «в работе 3».</summary>
    public static string Summary(IReadOnlyCollection<AgrConversionRow> rows)
    {
        int done = rows.Count(r => r.State == AgrRowState.Done);
        int err = rows.Count(r => r.State == AgrRowState.Error);
        int cancelled = rows.Count(r => r.State == AgrRowState.Cancelled);
        int active = rows.Count(r => !r.IsFinished);
        return Summary(rows.Count, done, err, cancelled, active);
    }

    public static string Summary(int total, int done, int errors, int cancelled, int active = 0)
    {
        var s = $"Готово {done} из {total}";
        if (errors > 0) s += $", {errors} {Plural(errors, "ошибка", "ошибки", "ошибок")}";
        if (cancelled > 0) s += $", отменено {cancelled}";
        if (active > 0) s += $", в работе {active}";
        return s;
    }

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    public static string Megabytes(long bytes) =>
        (bytes / 1e6).ToString(bytes >= 10_000_000 ? "0.0" : "0.00", CultureInfo.GetCultureInfo("ru-RU")) + " МБ";
}

/// <summary>Прогресс без контекста синхронизации: вызывается прямо в потоке конвертера.</summary>
sealed class InlineProgress<T> : IProgress<T>
{
    readonly Action<T> _action;
    public InlineProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}
