using System.ComponentModel;

namespace Connector.AgrConversion.Service;

/// <summary>Состояние строки очереди конвертера.</summary>
public enum AgrRowState
{
    /// <summary>В очереди: ждёт свободного места (по умолчанию по две части параллельно).</summary>
    Queued,
    /// <summary>Конвертируется: этап — <see cref="AgrConversionRow.Phase"/>, доля — <see cref="AgrConversionRow.Fraction"/>.</summary>
    Running,
    Done,
    Error,
    Cancelled,
}

/// <summary>
/// Строка очереди — одна часть АГР (zip или папка части). Меняется в рабочих потоках службы: событие
/// <see cref="PropertyChanged"/> (с пустым именем — «изменилось всё») и <see cref="AgrConverterService.RowChanged"/>
/// приходят не в потоке окна. <see cref="Elapsed"/> и <see cref="Remaining"/> считаются при чтении — окно обновляет
/// их своим таймером.
/// </summary>
public sealed class AgrConversionRow : INotifyPropertyChanged
{
    readonly object _gate = new();
    readonly TimeProvider _time;
    readonly Func<double> _secondsPerMb;
    long _startTs, _endTs;

    internal AgrConversionRow(string sourcePath, string name, long sourceBytes, TimeProvider time, Func<double> secondsPerMb)
    {
        _secondsPerMb = secondsPerMb;
        Id = Guid.NewGuid();
        SourcePath = sourcePath;
        Name = name;
        SourceBytes = sourceBytes;
        _time = time;
    }

    public Guid Id { get; }

    /// <summary>Полный путь к zip части или к папке распакованной части.</summary>
    public string SourcePath { get; }

    /// <summary>Имя части — имя архива без <c>.zip</c> (или имя папки): как имя модели в Студии (Р10.2).</summary>
    public string Name { get; }

    /// <summary>Имя файла результата: <c>&lt;часть&gt;.glb.zip</c>.</summary>
    public string OutputFileName => Name + AgrConverterPaths.OutputSuffix;

    public long SourceBytes { get; }

    public AgrRowState State { get; private set; }

    /// <summary>Этап конвертера (для строк <see cref="AgrRowState.Running"/>), null — ещё не начат.</summary>
    public AgrConvertPhase? Phase { get; private set; }

    /// <summary>Подпись шага конвертера, например «gltfpack L1» или «текстуры 12 из 36».</summary>
    public string StageText { get; private set; } = "";

    /// <summary>Пройденная доля 0…1 (у готовой — 1).</summary>
    public double Fraction { get; private set; }

    public int Percent => (int)Math.Floor(Fraction * 100);

    /// <summary>Текст ошибки для пользователя (<see cref="AgrRowState.Error"/>).</summary>
    public string? Error { get; private set; }

    /// <summary>Готовый <c>&lt;часть&gt;.glb.zip</c> в папке запуска.</summary>
    public string? ZipPath { get; private set; }
    public long ZipBytes { get; private set; }

    /// <summary>Отчёт чтения части (<c>Отчёты\&lt;часть&gt;.report.json</c>), вне zip.</summary>
    public string? ReportPath { get; private set; }

    /// <summary>Копия manifest.json части рядом с отчётом (<c>Отчёты\&lt;часть&gt;.manifest.json</c>).</summary>
    public string? ManifestPath { get; private set; }

    public int WarningCount { get; private set; }

    public bool IsFinished => State is AgrRowState.Done or AgrRowState.Error or AgrRowState.Cancelled;

    /// <summary>Отмена запрошена, строка ещё не остановилась (идёт чистка).</summary>
    public bool CancelRequested { get; private set; }

    /// <summary>У ошибки — этап, на котором строка упала («Уровни — gltfpack L1»; «проверка ввода» — не часть АГР).</summary>
    public string? FailedStage { get; private set; }

    /// <summary>Журнал службы: исключения обработчиков <see cref="PropertyChanged"/> и снятия gltfpack при отмене.</summary>
    internal Action<string>? Log { get; init; }

    /// <summary>Прошло с начала конвертации строки (у ожидающей — ноль).</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                if (_startTs == 0) return TimeSpan.Zero;
                return _time.GetElapsedTime(_startTs, _endTs != 0 ? _endTs : _time.GetTimestamp());
            }
        }
    }

    /// <summary>
    /// Осталось — оценка <see cref="AgrConversionEstimate.Remaining"/> по размеру архива и пройденной доле; у
    /// ожидающей — ожидаемое время по размеру; у законченной — null.
    /// </summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (IsFinished) return null;
            return AgrConversionEstimate.Remaining(Elapsed, Fraction, SourceBytes, _secondsPerMb());
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal CancellationTokenSource Cancellation { get; } = new();

    internal bool TryRequestCancel()
    {
        lock (_gate)
        {
            if (IsFinished || CancelRequested) return false;
            CancelRequested = true;
        }
        try { Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException ex)
        {
            // Обработчик отмены (снятие gltfpack) бросил: отмена всё равно запрошена, в поток окна не бросаем.
            Report($"{Name}: при отмене — {ex.InnerException?.Message ?? ex.Message}");
        }
        Changed();
        return true;
    }

    internal void MarkRunning()
    {
        lock (_gate)
        {
            State = AgrRowState.Running;
            _startTs = _time.GetTimestamp();
            StageText = "запуск";
        }
        Changed();
    }

    internal void MarkProgress(AgrConvertPhase phase, string stage, double fraction)
    {
        lock (_gate)
        {
            if (State != AgrRowState.Running) return;
            Phase = phase;
            StageText = stage;
            Fraction = Math.Max(Fraction, Math.Clamp(fraction, 0, 1)); // повторные сборки уровня не откатывают полосу
        }
        Changed();
    }

    /// <summary>Готово. Отмена, пришедшая после точки фиксации службы, сбрасывается: результат уже в папке запуска.</summary>
    internal void MarkDone(string zipPath, long zipBytes, string? reportPath, string? manifestPath, int warnings)
    {
        lock (_gate)
        {
            State = AgrRowState.Done;
            CancelRequested = false; // готово важнее поздней отмены
            Fraction = 1;
            StageText = "готово";
            ZipPath = zipPath;
            ZipBytes = zipBytes;
            ReportPath = reportPath;
            ManifestPath = manifestPath;
            WarningCount = warnings;
            Stop();
        }
        Changed();
    }

    internal void MarkError(string error)
    {
        lock (_gate)
        {
            FailedStage = State == AgrRowState.Running
                ? (Phase is AgrConvertPhase p ? $"{AgrConversionStages.Label(p)} — {StageText}" : StageText)
                : "проверка ввода";
            State = AgrRowState.Error;
            Error = error;
            StageText = "ошибка";
            Stop();
        }
        Changed();
    }

    internal void MarkCancelled()
    {
        lock (_gate)
        {
            State = AgrRowState.Cancelled;
            StageText = "отменено";
            Stop();
        }
        Changed();
    }

    void Stop()
    {
        if (_startTs != 0 && _endTs == 0) _endTs = _time.GetTimestamp();
    }

    /// <summary>Исключение обработчика <see cref="PropertyChanged"/> — в журнал службы, конвертацию не прерывает.</summary>
    void Changed()
    {
        var handler = PropertyChanged;
        if (handler == null) return;
        var args = new PropertyChangedEventArgs(string.Empty);
        foreach (var d in handler.GetInvocationList())
        {
            try { ((PropertyChangedEventHandler)d)(this, args); }
            catch (Exception ex) { Report($"{Name}: обработчик PropertyChanged бросил исключение: {ex.Message}"); }
        }
    }

    void Report(string line)
    {
        try { Log?.Invoke(line); }
        catch (Exception) { }
    }

    public override string ToString() => $"{Name}: {State} {Percent}% {StageText}";
}

/// <summary>Запуск — пачка частей с общей папкой результатов <c>&lt;корень&gt;\ГГГГ-ММ-ДД_ЧЧ-ММ</c>.</summary>
public sealed class AgrConversionRun
{
    readonly List<AgrConversionRow> _rows = new();

    internal AgrConversionRun(DateTimeOffset startedAt, string folder)
    {
        StartedAt = startedAt;
        Folder = folder;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; }

    /// <summary>Папка результатов запуска. Создаётся при переносе первой готовой части; у запуска без готовых частей её на диске нет.</summary>
    public string Folder { get; }

    public IReadOnlyList<AgrConversionRow> Rows
    {
        get { lock (_rows) return _rows.ToList(); }
    }

    public bool IsFinished
    {
        get { lock (_rows) return _rows.Count > 0 && _rows.All(r => r.IsFinished); }
    }

    /// <summary>Итог словами: «Готово 15 из 16, 1 ошибка».</summary>
    public string Summary => AgrConverterText.Summary(Rows);

    internal void Add(AgrConversionRow row)
    {
        lock (_rows) _rows.Add(row);
    }
}
