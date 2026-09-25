using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connector.AgrConversion.Service;

/// <summary>Настройки конвертера: корень результатов и число частей параллельно.</summary>
public sealed class AgrConverterSettings
{
    /// <summary>Корень результатов; null или пусто — <see cref="AgrConverterPaths.DefaultOutputRoot"/>.</summary>
    public string? OutputRoot { get; set; }

    public int Parallelism { get; set; } = AgrConverterConstants.DefaultParallelism;

    [JsonIgnore]
    public string EffectiveOutputRoot => string.IsNullOrWhiteSpace(OutputRoot) ? AgrConverterPaths.DefaultOutputRoot : OutputRoot!;

    [JsonIgnore]
    public int EffectiveParallelism => Math.Clamp(Parallelism, 1, AgrConverterConstants.MaxParallelism);
}

/// <summary>Часть в записи истории.</summary>
public sealed class AgrHistoryPart
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public AgrRowState State { get; set; }
    public string? Error { get; set; }
    public string? ZipPath { get; set; }
    public long ZipBytes { get; set; }
    public string? ReportPath { get; set; }
    public string? ManifestPath { get; set; }
    public double Seconds { get; set; }
    public int Warnings { get; set; }

    /// <summary>У ошибки — этап, на котором строка упала; иначе null.</summary>
    public string? Stage { get; set; }

    /// <summary>Сколько успела, % (у ошибки и отмены — где остановилась).</summary>
    public int Percent { get; set; }
}

/// <summary>Запись истории — один запуск: дата, части, итог, папка, пути отчётов.</summary>
public sealed class AgrHistoryRun
{
    public Guid Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Folder { get; set; } = "";
    public int Total { get; set; }
    public int Done { get; set; }
    public int Failed { get; set; }
    public int Cancelled { get; set; }
    public string Summary { get; set; } = "";
    public List<AgrHistoryPart> Parts { get; set; } = new();
}

/// <summary>
/// Хранилище конвертера рядом с настройками Коннектора (<c>%LOCALAPPDATA%\ConnectorAgentDesktop</c>):
/// <c>converter_settings.json</c> и <c>converter_history.json</c>. Битые файлы Коннектор не роняют.
/// </summary>
public sealed class AgrConverterStorage
{
    public const string SettingsFileName = "converter_settings.json";
    public const string HistoryFileName = "converter_history.json";

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public AgrConverterStorage(string? directory = null, Action<string>? log = null)
    {
        Directory = directory ?? AgrConverterPaths.DefaultStorageDirectory;
        Log = log;
    }

    public string Directory { get; }
    public string SettingsPath => Path.Combine(Directory, SettingsFileName);
    public string HistoryPath => Path.Combine(Directory, HistoryFileName);

    /// <summary>Журнал «Конвертер: …» (строка без префикса).</summary>
    public Action<string>? Log { get; set; }

    public AgrConverterSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AgrConverterSettings();
            return JsonSerializer.Deserialize<AgrConverterSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8), Json)
                   ?? new AgrConverterSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log?.Invoke($"настройки не прочитаны ({ex.Message}) — взяты по умолчанию");
            return new AgrConverterSettings();
        }
    }

    public void SaveSettings(AgrConverterSettings settings) =>
        WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, Json));

    internal static void WriteAtomic(string path, string text)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// История запусков между сеансами. «Убрать из истории» удаляет только запись — файлы результатов и отчётов остаются.
/// Битый или чужой файл — пустая история и строка в журнал; сам файл откладывается рядом (<c>.unreadable-…</c>), чтобы
/// следующая запись его не затёрла. Потокобезопасна.
/// </summary>
public sealed class AgrConverterHistory
{
    public const string Format = "structura-connector-converter-history";
    public const int Version = 1;
    public const int MaxRuns = 500;

    readonly object _gate = new();
    readonly List<AgrHistoryRun> _runs = new();
    readonly Action<string>? _log;

    public AgrConverterHistory(string path, Action<string>? log = null)
    {
        Path = path;
        _log = log;
    }

    public string Path { get; }

    /// <summary>Записи, новые сверху.</summary>
    public IReadOnlyList<AgrHistoryRun> Runs
    {
        get { lock (_gate) return _runs.OrderByDescending(r => r.StartedAt).ToList(); }
    }

    public event EventHandler? Changed;

    sealed class FileModel
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public List<AgrHistoryRun?>? Runs { get; set; }
    }

    /// <summary>Читает файл. Нет файла — пусто; битый или чужой — пусто и строка в журнал. Не бросает.</summary>
    public void Load()
    {
        List<AgrHistoryRun> loaded = new();
        string? problem = null;
        try
        {
            if (File.Exists(Path))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(Path, Encoding.UTF8), AgrConverterStorage.Json);
                if (model == null || model.Format != Format)
                {
                    problem = "чужой файл — нет метки формата истории конвертера";
                }
                else if (model.Version != Version)
                {
                    problem = $"версия {model.Version}, ожидалась {Version}";
                }
                else
                {
                    loaded = (model.Runs ?? new()).Where(r => r != null && r.Id != Guid.Empty).Select(r => r!).ToList();
                    foreach (var r in loaded) r.Parts ??= new();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException
                                       or InvalidOperationException or ArgumentException)
        {
            problem = ex.Message;
        }
        if (problem != null)
        {
            loaded = new();
            string aside = Path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Move(Path, aside);
                _log?.Invoke($"история не прочитана ({problem}) — начата пустая; прежний файл: {aside}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"история не прочитана ({problem}) — начата пустая; прежний файл не отложен: {ex.Message}");
            }
        }
        lock (_gate)
        {
            _runs.Clear();
            _runs.AddRange(loaded);
        }
        RaiseChanged();
    }

    /// <summary>Добавляет или заменяет запись запуска (по <see cref="AgrHistoryRun.Id"/>) и сохраняет файл.</summary>
    public void Record(AgrHistoryRun run)
    {
        Upsert(run);
        RaiseChanged();
    }

    /// <summary><see cref="Record"/> без события: служба пишет снимок под своим замком, событие — после.</summary>
    internal void Upsert(AgrHistoryRun run)
    {
        lock (_gate)
        {
            int i = _runs.FindIndex(r => r.Id == run.Id);
            if (i >= 0) _runs[i] = run; else _runs.Add(run);
            if (_runs.Count > MaxRuns)
            {
                _runs.Sort((a, b) => a.StartedAt.CompareTo(b.StartedAt));
                _runs.RemoveRange(0, _runs.Count - MaxRuns);
            }
            SaveLocked();
        }
    }

    /// <summary>«Убрать из истории»: только запись; папка, <c>.glb.zip</c> и отчёты остаются на диске.</summary>
    public bool Remove(Guid runId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _runs.RemoveAll(r => r.Id == runId) > 0;
            if (removed) SaveLocked();
        }
        if (removed) RaiseChanged();
        return removed;
    }

    /// <summary><see cref="Changed"/> защищённо: исключение обработчика — в журнал.</summary>
    internal void RaiseChanged()
    {
        var handler = Changed;
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((EventHandler)d)(this, EventArgs.Empty); }
            catch (Exception ex)
            {
                try { _log?.Invoke($"обработчик истории бросил исключение: {ex.Message}"); } catch (Exception) { }
            }
        }
    }

    void SaveLocked()
    {
        var model = new FileModel { Format = Format, Version = Version, Runs = _runs.Cast<AgrHistoryRun?>().ToList() };
        try
        {
            AgrConverterStorage.WriteAtomic(Path, JsonSerializer.Serialize(model, AgrConverterStorage.Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"история не сохранена: {ex.Message}");
        }
    }
}
