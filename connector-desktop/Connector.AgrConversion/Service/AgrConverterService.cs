using System.Text.Encodings.Web;
using System.Text.Json;

namespace Connector.AgrConversion.Service;

/// <summary>Общий ход запуска: для строки шага, полосы и ETA над таблицей.</summary>
public sealed record AgrConversionOverall(int Total, int Done, int Failed, int Cancelled, int Running, int Queued,
                                          double Fraction, TimeSpan? Remaining, string Summary);

/// <summary>Параметры службы, которые не хранятся в настройках (для тестов и C2b).</summary>
public sealed class AgrConverterServiceOptions
{
    /// <summary>Корень временных папок строк; null — <see cref="AgrConverterPaths.DefaultWorkRoot"/>.</summary>
    public string? WorkRoot { get; init; }

    public TimeProvider? Time { get; init; }

    /// <summary>Журнал с самого создания службы: строки о битой истории или настройках пишутся ещё в конструкторе.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Служба конвертера без интерфейса (C2a): очередь частей АГР, по <see cref="Parallelism"/> параллельно (по умолчанию 2),
/// отмена строки и всех, папка запуска <c>&lt;корень&gt;\ГГГГ-ММ-ДД_ЧЧ-ММ</c>, история между сеансами, отчёт части.
/// <para>
/// Потоки: <see cref="Add"/>, <see cref="Cancel"/>, <see cref="CancelAll"/> не блокируют; строки конвертируются в
/// пуле потоков. События <see cref="RowChanged"/>, <see cref="RowsChanged"/>, <see cref="RunFinished"/> и делегат
/// <see cref="Log"/> вызываются из рабочих потоков — окно переводит их в свой поток само. Исключение обработчика (и
/// <see cref="AgrConversionRow.PropertyChanged"/>) — строка в журнал, очередь идёт дальше.
/// </para>
/// Сеть не используется: Коннектор в Структуру ничего не отправляет (Р9).
/// </summary>
public sealed class AgrConverterService : IDisposable
{
    static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly object _gate = new();
    readonly IAgrPartConverter _converter;
    readonly AgrConverterStorage _storage;
    readonly TimeProvider _time;
    readonly string _workRoot;
    readonly List<AgrConversionRow> _pending = new();
    readonly Dictionary<Guid, Task> _active = new();
    AgrConverterSettings _settings;
    AgrConversionRun? _run;
    double _doneSeconds, _doneMb;
    bool _disposed;
    readonly object _historyGate = new();

    public AgrConverterService(IAgrPartConverter converter, AgrConverterStorage? storage = null, AgrConverterServiceOptions? options = null)
    {
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        Log = options?.Log;
        _storage = storage ?? new AgrConverterStorage();
        _storage.Log ??= line => Write(line);
        _time = options?.Time ?? TimeProvider.System;
        _workRoot = Path.GetFullPath(options?.WorkRoot ?? AgrConverterPaths.DefaultWorkRoot);
        _settings = _storage.LoadSettings();
        History = new AgrConverterHistory(_storage.HistoryPath, line => Write(line));
        History.Load();
        CleanupStaleWork();
    }

    /// <summary>Строки «Конвертер: …» в общий журнал окна (как <c>vpn.Log = AppendLog</c>).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Строка изменилась: состояние, этап, доля, ошибка.</summary>
    public event EventHandler<AgrConversionRow>? RowChanged;

    /// <summary>Состав строк изменился: добавлены строки или начат новый запуск.</summary>
    public event EventHandler? RowsChanged;

    /// <summary>Все строки запуска закончены (готово, ошибка или отменено); запись истории уже сохранена.</summary>
    public event EventHandler<AgrConversionRun>? RunFinished;

    public AgrConverterHistory History { get; }

    public AgrConverterStorage Storage => _storage;

    public AgrConversionRun? CurrentRun
    {
        get { lock (_gate) return _run; }
    }

    public IReadOnlyList<AgrConversionRow> Rows => CurrentRun?.Rows ?? Array.Empty<AgrConversionRow>();

    /// <summary>Корень результатов (настройка, хранится).</summary>
    public string OutputRoot
    {
        get { lock (_gate) return _settings.EffectiveOutputRoot; }
    }

    /// <summary>Сколько частей конвертируется одновременно (настройка, хранится; по умолчанию 2).</summary>
    public int Parallelism
    {
        get { lock (_gate) return _settings.EffectiveParallelism; }
    }

    /// <summary>Сменить корень результатов. Действует со следующего запуска; текущий пишет в свою папку.</summary>
    public void SetOutputRoot(string? root)
    {
        string? value = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
        lock (_gate) _settings.OutputRoot = value;
        SaveSettings();
        Write($"папка результатов — {OutputRoot}");
    }

    public void SetParallelism(int parallelism)
    {
        lock (_gate) _settings.Parallelism = Math.Clamp(parallelism, 1, AgrConverterConstants.MaxParallelism);
        SaveSettings();
        Pump();
    }

    /// <summary>
    /// Добавляет zip частей, папки с zip или папки частей в текущий запуск (нет запуска — создаётся) и сразу
    /// запускает очередь. Не часть АГР — строка с ошибкой. Тот же файл дважды в одном запуске не добавляется; две
    /// части с одним именем — вторая с ошибкой (одинаковый <c>.glb.zip</c>). Возвращает новые строки.
    /// </summary>
    public IReadOnlyList<AgrConversionRow> Add(IEnumerable<string> paths)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var items = AgrConverterInput.Collect(paths);
        var added = new List<AgrConversionRow>();
        var finishedNow = new List<AgrConversionRow>();
        var skipped = new List<string>();
        AgrConversionRun run;
        lock (_gate)
        {
            run = _run ??= NewRunLocked();
            var existing = run.Rows;
            foreach (var item in items)
            {
                if (existing.Concat(added).Any(r => string.Equals(r.SourcePath, item.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped.Add(item.Name);
                    continue;
                }
                var row = new AgrConversionRow(item.Path, item.Name, item.Bytes, _time, CurrentSecondsPerMb) { Log = Write };
                string? error = item.Error;
                var twin = existing.Concat(added).FirstOrDefault(r => string.Equals(r.Name, item.Name, StringComparison.OrdinalIgnoreCase));
                if (error == null && twin != null)
                {
                    error = $"{item.Name}: часть с таким именем уже есть в этом запуске ({twin.SourcePath}) — результат был бы тот же {row.OutputFileName}";
                }
                run.Add(row);
                added.Add(row);
                _finishedRuns.Remove(run.Id);
                if (error != null)
                {
                    row.MarkError(error);
                    finishedNow.Add(row);
                }
                else
                {
                    _pending.Add(row);
                }
            }
        }
        foreach (var name in skipped) Write($"{name} — уже в очереди, повтор пропущен");
        if (added.Count > 0)
        {
            Write($"добавлено {added.Count} {AgrConverterText.Plural(added.Count, "часть", "части", "частей")} — папка {run.Folder}");
            Raise(RowsChanged, nameof(RowsChanged));
        }
        foreach (var row in finishedNow)
        {
            Write($"{row.Name} — ошибка: {row.Error}");
            RaiseRowChanged(row);
        }
        if (finishedNow.Count > 0) RecordHistory(run);
        Pump();
        CheckRunFinished(run);
        return added;
    }

    /// <summary>«Отменить» строки: ожидающая снимается сразу, идущая останавливается и чистит временные файлы.</summary>
    public bool Cancel(Guid rowId)
    {
        AgrConversionRow? row;
        bool wasPending;
        AgrConversionRun? run;
        lock (_gate)
        {
            run = _run;
            row = run?.Rows.FirstOrDefault(r => r.Id == rowId);
            if (row == null || row.IsFinished) return false;
            wasPending = _pending.Remove(row);
        }
        if (wasPending)
        {
            row.MarkCancelled();
            Write($"{row.Name} — отменено до начала");
            RaiseRowChanged(row);
            RecordHistory(run!);
            CheckRunFinished(run!);
            return true;
        }
        if (row.TryRequestCancel())
        {
            Write($"{row.Name} — отмена…");
            RaiseRowChanged(row);
            return true;
        }
        return false;
    }

    /// <summary>«Отменить всё»: все ожидающие и идущие строки текущего запуска. Возвращает число отменённых.</summary>
    public int CancelAll()
    {
        var ids = Rows.Where(r => !r.IsFinished).Select(r => r.Id).ToList();
        // Сначала ожидающие — чтобы освободившееся место не взяла следующая из очереди.
        List<Guid> pending;
        lock (_gate) pending = _pending.Select(r => r.Id).ToList();
        int n = 0;
        foreach (var id in pending.Concat(ids.Except(pending)))
        {
            if (Cancel(id)) n++;
        }
        if (n > 0) Write($"отменить всё — {n}");
        return n;
    }

    /// <summary>«Новая конвертация»: следующий <see cref="Add"/> начнёт новый запуск со своей папкой. false — текущий ещё идёт.</summary>
    public bool NewRun()
    {
        lock (_gate)
        {
            if (_run != null && !_run.IsFinished && _run.Rows.Count > 0) return false;
            _run = null;
        }
        Raise(RowsChanged, nameof(RowsChanged));
        return true;
    }

    /// <summary>Ждёт, пока очередь опустеет и все строки остановятся.</summary>
    public async Task WaitIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] tasks;
            lock (_gate)
            {
                if (_pending.Count == 0 && _active.Count == 0) return;
                tasks = _active.Values.ToArray();
            }
            if (tasks.Length == 0)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                continue;
            }
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Общий ход текущего запуска: доля по размеру архивов, оценка «осталось» с учётом параллельности.</summary>
    public AgrConversionOverall GetOverall()
    {
        var rows = Rows;
        int done = rows.Count(r => r.State == AgrRowState.Done);
        int failed = rows.Count(r => r.State == AgrRowState.Error);
        int cancelled = rows.Count(r => r.State == AgrRowState.Cancelled);
        int running = rows.Count(r => r.State == AgrRowState.Running);
        int queued = rows.Count(r => r.State == AgrRowState.Queued);
        var live = rows.Where(r => r.State is AgrRowState.Done or AgrRowState.Running or AgrRowState.Queued).ToList();
        double bytes = live.Sum(r => (double)Math.Max(1, r.SourceBytes));
        double fraction = live.Count == 0 ? (rows.Count > 0 ? 1 : 0) : live.Sum(r => Math.Max(1, r.SourceBytes) * r.Fraction) / bytes;
        TimeSpan? remaining = null;
        if (running + queued > 0)
        {
            double sum = live.Where(r => !r.IsFinished).Sum(r => r.Remaining?.TotalSeconds ?? 0);
            remaining = TimeSpan.FromSeconds(Math.Round(sum / Math.Max(1, Math.Min(Parallelism, running + queued))));
        }
        return new AgrConversionOverall(rows.Count, done, failed, cancelled, running, queued, fraction, remaining, AgrConverterText.Summary(rows));
    }

    /// <summary>
    /// Отчёт части строки (читает файлы, конвертацию не запускает). Не бросает: у строки с ошибкой, отменённой или с
    /// пропавшими файлами — отчёт-ошибка (<see cref="AgrPartReport.IsFailure"/>: этап, текст ошибки, доля, пути).
    /// </summary>
    public static AgrPartReport LoadReport(AgrConversionRow row) => row.State == AgrRowState.Done
        ? AgrPartReport.TryLoad(row.Name, row.SourcePath, row.ManifestPath, row.ReportPath, row.ZipPath)
        : AgrPartReport.Failure(row.Name, row.SourcePath, row.State, row.Error ?? StateText(row.State), row.FailedStage, row.Percent,
                                row.ZipPath, row.ReportPath, row.ManifestPath);

    /// <summary>Отчёт части из истории; не бросает (см. <see cref="LoadReport(AgrConversionRow)"/>).</summary>
    public static AgrPartReport LoadReport(AgrHistoryPart part) => part.State == AgrRowState.Done
        ? AgrPartReport.TryLoad(part.Name, part.Source, part.ManifestPath, part.ReportPath, part.ZipPath)
        : AgrPartReport.Failure(part.Name, part.Source, part.State, part.Error ?? StateText(part.State), part.Stage, part.Percent,
                                part.ZipPath, part.ReportPath, part.ManifestPath);

    static string StateText(AgrRowState state) => state switch
    {
        AgrRowState.Cancelled => "отменено — результата нет",
        AgrRowState.Queued => "в очереди — отчёта ещё нет",
        AgrRowState.Running => "конвертируется — отчёта ещё нет",
        _ => "результата нет",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAll();
    }

    /// <summary>
    /// Закрытие окна (ревью C2b): отменить все части и дождаться, пока идущие запишут итог в историю, но не дольше
    /// <paramref name="wait"/>. Не успели (конвертер не отпустил отмену) — запись останется без
    /// <see cref="AgrHistoryRun.FinishedAt"/> и при следующем старте закроется как «Прервано». Потоки частей поток окна
    /// не ждут (журнал окна — через BeginInvoke), поэтому ожидание из него не заклинивает.
    /// </summary>
    /// <returns>true — все части остановились и записаны.</returns>
    public bool Shutdown(TimeSpan wait)
    {
        Dispose();
        Task[] tasks;
        lock (_gate) tasks = _active.Values.ToArray();
        if (tasks.Length == 0) return true;
        try
        {
            return Task.WhenAll(tasks).Wait(wait);
        }
        catch (AggregateException)
        {
            return true; // задача части завершилась (сбоем) — ждать больше нечего
        }
    }

    // ---- очередь ----

    double CurrentSecondsPerMb()
    {
        lock (_gate) return _doneMb >= 1 ? _doneSeconds / _doneMb : AgrConversionEstimate.DefaultSecondsPerMb;
    }

    AgrConversionRun NewRunLocked()
    {
        // Папка только выбирается: на диске она появится с первой готовой частью (Execute). Отменённый целиком запуск
        // пустой папки с датой не оставляет; недоступный корень — ошибка строки при переносе, не исключение Add.
        var now = _time.GetLocalNow();
        string root = _settings.EffectiveOutputRoot;
        string folder;
        try
        {
            // Папки запусков из истории — тоже заняты: у запуска, отменённого целиком, папки на диске нет, и новый
            // запуск в ту же минуту иначе получил бы тот же путь (две записи истории с одной папкой, ревью C2a).
            // Порядок замков: служба → история (история замок службы не берёт).
            var used = new HashSet<string>(History.Runs.Select(r => r.Folder), StringComparer.OrdinalIgnoreCase);
            folder = AgrConverterPaths.FreeRunFolder(root, now, used.Contains);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            folder = Path.Combine(root, AgrConverterPaths.RunFolderName(now));
        }
        return new AgrConversionRun(now, folder);
    }

    void Pump()
    {
        var start = new List<(AgrConversionRow Row, AgrConversionRun Run)>();
        lock (_gate)
        {
            if (_disposed || _run == null) return;
            while (_active.Count + start.Count < _settings.EffectiveParallelism && _pending.Count > 0)
            {
                var row = _pending[0];
                _pending.RemoveAt(0);
                start.Add((row, _run));
            }
            foreach (var (row, run) in start)
            {
                _active[row.Id] = new Task(() => Execute(row, run), TaskCreationOptions.LongRunning);
            }
        }
        // События строки — вне замка службы: обработчик окна может синхронно уйти в свой поток.
        foreach (var (row, _) in start)
        {
            row.MarkRunning();
            Write($"{row.Name} — начата");
            RaiseRowChanged(row);
            Task task;
            lock (_gate) task = _active[row.Id];
            task.Start();
        }
    }

    void Execute(AgrConversionRow row, AgrConversionRun run)
    {
        var ct = row.Cancellation.Token;
        string work = Path.Combine(_workRoot, AgrConverterPaths.RowWorkPrefix + row.Id.ToString("N"));
        string final = Path.Combine(run.Folder, row.OutputFileName);
        var placed = new List<string>();
        try
        {
            AgrRowState outcome;
            string? error = null, reportPath = null, manifestPath = null, cancelNote = null;
            int warnings = 0;
            try
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(work);
                var progress = new InlineProgress<AgrConvertProgress>(p =>
                {
                    row.MarkProgress(p.Phase, p.Stage, AgrConversionStages.Overall(p.Phase, p.PhaseFraction));
                    RaiseRowChanged(row);
                });
                var result = _converter.Convert(row.SourcePath, Path.Combine(work, "out"), Path.Combine(work, "tmp"), progress, ct);
                // Точка фиксации: до переноса в папку запуска отмена ещё действует — результата не будет. Отмена,
                // пришедшая позже, готовую часть не отменяет: готово важнее (AgrConversionRow.MarkDone сбрасывает запрос).
                ct.ThrowIfCancellationRequested();

                try
                {
                    Directory.CreateDirectory(run.Folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    throw new RunFolderException($"папка результатов недоступна: {run.Folder} — {ex.Message}", ex);
                }
                string part = final + ".part";
                placed.Add(part);
                File.Move(result.ZipPath, part, overwrite: true);
                placed.Add(final);
                File.Move(part, final, overwrite: true);
                string reports = Path.Combine(run.Folder, AgrConverterPaths.ReportsFolder);
                Directory.CreateDirectory(reports);
                if (result.PartReportJson != null)
                {
                    reportPath = Path.Combine(reports, row.Name + AgrConverterPaths.ReportSuffix);
                    placed.Add(reportPath);
                    AgrConverterStorage.WriteAtomic(reportPath, result.PartReportJson);
                }
                if (result.Manifest != null)
                {
                    manifestPath = Path.Combine(reports, row.Name + AgrConverterPaths.ManifestSuffix);
                    placed.Add(manifestPath);
                    AgrConverterStorage.WriteAtomic(manifestPath, result.Manifest.ToJsonString(ManifestJson));
                }
                warnings = result.Manifest?["parts"]?[0]?["warnings"]?.AsArray().Count ?? result.Warnings.Count;
                outcome = AgrRowState.Done;
            }
            catch (OperationCanceledException oce) when (ct.IsCancellationRequested)
            {
                DeletePlaced(placed);
                outcome = AgrRowState.Cancelled;
                if (oce.InnerException != null) cancelNote = oce.Message;
            }
            catch (Exception ex)
            {
                DeletePlaced(placed);
                outcome = AgrRowState.Error;
                error = ErrorText(ex);
            }

            // Чистка — до смены состояния: «отменено» и «ошибка» видны, когда временных файлов уже нет.
            CleanupWork(work, final + ".part");

            switch (outcome)
            {
                case AgrRowState.Done:
                    long bytes = new FileInfo(final).Length;
                    bool lateCancel = row.CancelRequested;
                    row.MarkDone(final, bytes, reportPath, manifestPath, warnings);
                    lock (_gate)
                    {
                        if (row.SourceBytes > 0)
                        {
                            _doneSeconds += row.Elapsed.TotalSeconds;
                            _doneMb += row.SourceBytes / 1e6;
                        }
                    }
                    Write($"{row.Name} — готово за {AgrConverterText.Duration(row.Elapsed)}, {AgrConverterText.Megabytes(bytes)}" +
                          (warnings > 0 ? $", предупреждений {warnings}" : "") + $": {final}" +
                          (lateCancel ? " (отмена пришла после переноса результата — часть сохранена)" : ""));
                    break;
                case AgrRowState.Cancelled:
                    row.MarkCancelled();
                    Write($"{row.Name} — отменено, временные файлы удалены" + (cancelNote != null ? $" ({cancelNote})" : ""));
                    break;
                default:
                    row.MarkError(error ?? "ошибка");
                    Write($"{row.Name} — ошибка: {row.Error}");
                    break;
            }
        }
        catch (Exception ex)
        {
            // Сбой самой службы (например, диск) — строка не должна зависнуть «в работе».
            if (!row.IsFinished) row.MarkError("сбой службы конвертера: " + ex.Message);
        }
        finally
        {
            // Каждый шаг отдельно: сбой одного (история, обработчик) не оставляет очередь стоять.
            lock (_gate) _active.Remove(row.Id);
            RaiseRowChanged(row);
            Guarded(() => RecordHistory(run), "история");
            Guarded(Pump, "очередь");
            Guarded(() => CheckRunFinished(run), "итог запуска");
        }
    }

    /// <summary>Временная папка строки (распаковка, glTF, уровни, недописанный zip) и <c>.part</c> в папке запуска.</summary>
    void CleanupWork(string work, string partialZip)
    {
        try { if (File.Exists(partialZip)) File.Delete(partialZip); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Write($"не удалён {partialZip}: {ex.Message}"); }
        if (!Directory.Exists(work)) return;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(work, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Снятый gltfpack отпускает файлы не сразу.
                if (attempt >= 20)
                {
                    Write($"временная папка не удалена ({ex.Message}): {work}");
                    return;
                }
                Thread.Sleep(100);
            }
        }
    }

    void DeletePlaced(List<string> placed)
    {
        foreach (var p in placed)
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Хвосты прошлых сеансов (Коннектор закрыли посреди конвертации): <c>row-*</c> в корне временных папок.</summary>
    void CleanupStaleWork()
    {
        try
        {
            if (!Directory.Exists(_workRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(_workRoot, AgrConverterPaths.RowWorkPrefix + "*"))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static string ErrorText(Exception ex) => ex switch
    {
        RunFolderException => ex.Message,
        AgrReadException r => r.Message,
        InvalidDataException or FileNotFoundException or DirectoryNotFoundException => ex.Message,
        IOException io when AgrReadException.IsLocked(io) => "файл занят другим процессом — " + ex.Message,
        _ => "сбой конвертера: " + ex.Message,
    };

    void CheckRunFinished(AgrConversionRun run)
    {
        lock (_gate)
        {
            if (!run.IsFinished || _finishedRuns.Contains(run.Id)) return;
            // Строки ещё в пути (finally не дошёл) — дождаться последнего.
            if (_active.Count > 0 && ReferenceEquals(_run, run)) return;
            _finishedRuns.Add(run.Id);
        }
        Write($"запуск закончен — {run.Summary}; папка {run.Folder}");
        Raise(RunFinished, run, nameof(RunFinished));
    }

    readonly HashSet<Guid> _finishedRuns = new();

    /// <summary>
    /// Снимок запуска в историю. Сборка снимка и запись — под одним замком: снимок, собранный раньше, не ляжет поверх
    /// более нового; последний записанный всегда полный (все части, <see cref="AgrHistoryRun.FinishedAt"/>).
    /// </summary>
    void RecordHistory(AgrConversionRun run)
    {
        lock (_historyGate)
        {
            if (!RecordHistoryLocked(run)) return;
        }
        History.RaiseChanged(); // вне замка: обработчик окна может синхронно уйти в свой поток
    }

    /// <summary>Тестам: снимок собран, ещё не записан (под замком истории) — задержкой здесь проверяется порядок записей.</summary>
    internal Action<AgrHistoryRun>? OnHistorySnapshot { get; set; }

    bool RecordHistoryLocked(AgrConversionRun run)
    {
        var rows = run.Rows;
        var finished = rows.Where(r => r.IsFinished).ToList();
        if (finished.Count == 0) return false;
        var entry = new AgrHistoryRun
        {
            Id = run.Id,
            StartedAt = run.StartedAt,
            FinishedAt = rows.All(r => r.IsFinished) ? _time.GetLocalNow() : null,
            Folder = run.Folder,
            Total = rows.Count,
            Done = rows.Count(r => r.State == AgrRowState.Done),
            Failed = rows.Count(r => r.State == AgrRowState.Error),
            Cancelled = rows.Count(r => r.State == AgrRowState.Cancelled),
            Summary = AgrConverterText.Summary(rows),
            Parts = finished.Select(r => new AgrHistoryPart
            {
                Name = r.Name, Source = r.SourcePath, State = r.State, Error = r.Error, ZipPath = r.ZipPath, ZipBytes = r.ZipBytes,
                ReportPath = r.ReportPath, ManifestPath = r.ManifestPath, Seconds = Math.Round(r.Elapsed.TotalSeconds, 1),
                Warnings = r.WarningCount, Stage = r.FailedStage, Percent = r.Percent,
            }).ToList(),
        };
        OnHistorySnapshot?.Invoke(entry);
        History.Upsert(entry);
        return true;
    }

    void SaveSettings()
    {
        AgrConverterSettings copy;
        lock (_gate) copy = new AgrConverterSettings { OutputRoot = _settings.OutputRoot, Parallelism = _settings.Parallelism };
        try
        {
            _storage.SaveSettings(copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Write($"настройки не сохранены: {ex.Message}");
        }
    }

    void Write(string line)
    {
        try { Log?.Invoke(AgrConverterConstants.LogPrefix + line); }
        catch (Exception) { /* журнал окна не должен ронять очередь */ }
    }

    void RaiseRowChanged(AgrConversionRow row) => Raise(RowChanged, row, nameof(RowChanged));

    /// <summary>События — защищённо, как <see cref="Log"/>: исключение обработчика — в журнал, очередь не встаёт.</summary>
    void Raise<T>(EventHandler<T>? handler, T arg, string name)
    {
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((EventHandler<T>)d)(this, arg); }
            catch (Exception ex) { Write($"обработчик {name} бросил исключение: {ex.Message}"); }
        }
    }

    void Raise(EventHandler? handler, string name)
    {
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((EventHandler)d)(this, EventArgs.Empty); }
            catch (Exception ex) { Write($"обработчик {name} бросил исключение: {ex.Message}"); }
        }
    }

    void Guarded(Action action, string what)
    {
        try { action(); }
        catch (Exception ex) { Write($"сбой ({what}): {ex.Message}"); }
    }

    sealed class RunFolderException(string message, Exception inner) : IOException(message, inner);
}
