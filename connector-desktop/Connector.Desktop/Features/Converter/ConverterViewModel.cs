using System.Collections.ObjectModel;
using System.Globalization;
using Connector.AgrConversion.Service;
using Connector.Desktop.Mvvm;

namespace Connector.Desktop.Features.Converter;

public enum ConverterScreen
{
    /// <summary>Кадр 1: запуска нет — зона перетаскивания.</summary>
    Intake,
    /// <summary>Кадр 2: идёт конвертация.</summary>
    Running,
    /// <summary>Кадр 3: все части закончены.</summary>
    Finished,
}

/// <summary>То, что окно делает само: диалоги, Проводник, браузер, буфер обмена, окно «Отчёт».</summary>
public interface IConverterUi
{
    IReadOnlyList<string>? PickZips();
    string? PickFolder();
    string? PickOutputRoot(string current);
    void OpenFolder(string folder);
    void ShowInFolder(string file);
    void OpenUrl(string url);
    void CopyText(string text);
    void ShowReport(ConverterReportViewModel report);
}

/// <summary>Состояние запуска для экрана: из службы (<see cref="ConverterViewModel.Refresh"/>) или готовое (кадры для отрисовки).</summary>
public sealed record ConverterSnapshot
{
    public IReadOnlyList<ConverterRowSnapshot> Rows { get; init; } = Array.Empty<ConverterRowSnapshot>();
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? Folder { get; init; }
    public double Fraction { get; init; }
    public TimeSpan? Remaining { get; init; }
    public DateTimeOffset Now { get; init; }
}

/// <summary>
/// Вид-модель вкладки «Конвертер» (кадры 1–3, 5). Не зависит от окна: события службы приходят из рабочих потоков и
/// переводятся в поток окна через <c>post</c> (в приложении — <see cref="ConverterDispatch.Post"/>, только
/// <c>BeginInvoke</c>: служба пишет журнал, держа свои замки, и синхронный вызов окна их бы заклинил).
/// </summary>
public sealed class ConverterViewModel : ObservableObject, IConverterRowActions, IDisposable
{
    readonly AgrConverterService _service;
    readonly IConverterUi _ui;
    readonly Action<Action> _post;
    readonly TimeProvider _time;
    readonly List<string> _pendingLog = new();
    readonly Dictionary<Guid, DateTimeOffset> _rowStarted = new();
    Action<string>? _log;
    int _refreshQueued;
    bool _disposed;
    ConverterSnapshot _last = new();
    readonly EventHandler<AgrConversionRow> _onRow;
    readonly EventHandler _onRows;
    readonly EventHandler<AgrConversionRun> _onRun;

    /// <summary>Сколько при закрытии окна ждать, пока отменённые части запишут историю (ревью C2b).</summary>
    public static readonly TimeSpan DefaultShutdownWait = TimeSpan.FromSeconds(2.5);

    readonly TimeSpan _shutdownWait;

    public ConverterViewModel(Func<Action<string>, AgrConverterService> createService, IConverterUi ui, Action<Action> post,
                              TimeProvider? time = null, TimeSpan? shutdownWait = null)
    {
        _shutdownWait = shutdownWait ?? DefaultShutdownWait;
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _time = time ?? TimeProvider.System;
        _service = createService(ServiceLog);
        _onRow = (_, _) => QueueRefresh();
        _onRows = (_, _) => QueueRefresh();
        _onRun = (_, _) => QueueRefresh();
        _service.RowChanged += _onRow;
        _service.RowsChanged += _onRows;
        _service.RunFinished += _onRun;
        _service.History.Changed += OnHistoryChanged;

        AddZipsCommand = new RelayCommand(() => { var p = _ui.PickZips(); if (p is { Count: > 0 }) AddPaths(p); });
        AddFolderCommand = new RelayCommand(() => { var p = _ui.PickFolder(); if (!string.IsNullOrWhiteSpace(p)) AddPaths(new[] { p }); });
        ChangeOutputRootCommand = new RelayCommand(ChangeOutputRoot);
        CancelAllCommand = new RelayCommand(() => { _service.CancelAll(); Refresh(); }, () => Screen == ConverterScreen.Running);
        OpenResultsCommand = new RelayCommand(OpenResults, () => !string.IsNullOrEmpty(_last.Folder));
        NewRunCommand = new RelayCommand(NewRun, () => Screen == ConverterScreen.Finished);
        OpenStudioCommand = new RelayCommand(() => _ui.OpenUrl(AgrConverterConstants.StudioUrl));

        Refresh();
        RefreshHistory();
    }

    public AgrConverterService Service => _service;

    /// <summary>Строки «Конвертер: …» в общий журнал окна. До привязки строки копятся и выводятся при установке.</summary>
    public Action<string>? Log
    {
        get => _log;
        set
        {
            _log = value;
            if (value == null) return;
            var lines = _pendingLog.ToArray();
            _pendingLog.Clear();
            foreach (var line in lines) value(line);
        }
    }

    public ObservableCollection<ConverterRowViewModel> Rows { get; } = new();
    public ObservableCollection<ConverterHistoryItemViewModel> History { get; } = new();

    public ConverterScreen Screen { get; private set; }
    public bool IsIntake => Screen == ConverterScreen.Intake;
    public bool IsRunning => Screen == ConverterScreen.Running;
    public bool IsFinished => Screen == ConverterScreen.Finished;

    // Кадр 1.
    public string OutputRootText { get; private set; } = "";
    public string ParallelHint { get; private set; } = "";

    // Кадр 2.
    public string ProgressTitle { get; private set; } = "";
    public int OverallPercent { get; private set; }
    public string OverallMeta { get; private set; } = "";

    // Кадр 3.
    public string SummaryDoneText { get; private set; } = "";
    public string SummarySeparator { get; private set; } = "";
    public string SummaryErrorText { get; private set; } = "";
    public string SummaryDoneColor { get; private set; } = ConverterColors.Ok;
    public string FinishedMeta { get; private set; } = "";
    public string StudioHint { get; private set; } = "";
    public string ResultNote { get; private set; } = "";

    // Кадр 5.
    public bool HistoryEmpty => History.Count == 0;

    public RelayCommand AddZipsCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand ChangeOutputRootCommand { get; }
    public RelayCommand CancelAllCommand { get; }
    public RelayCommand OpenResultsCommand { get; }
    public RelayCommand NewRunCommand { get; }
    public RelayCommand OpenStudioCommand { get; }

    /// <summary>Перетаскивание и кнопки «Добавить…». После законченного запуска сначала начинается новый.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0 || _disposed) return;
        if (Screen == ConverterScreen.Finished) _service.NewRun();
        _service.Add(list);
        Refresh();
    }

    /// <summary>Раз в секунду (таймер окна): «прошло» и «осталось» идут и без событий службы.</summary>
    public void Tick()
    {
        if (Screen == ConverterScreen.Running) Refresh();
    }

    /// <summary>Снимает состояние службы (в потоке окна).</summary>
    public void Refresh()
    {
        if (_disposed) return;
        var run = _service.CurrentRun;
        var rows = _service.Rows.Select(ConverterRowSnapshot.Of).ToList();
        var overall = _service.GetOverall();
        DateTimeOffset? finished = null;
        if (run != null && run.IsFinished)
        {
            finished = _service.History.Runs.FirstOrDefault(r => r.Id == run.Id)?.FinishedAt;
        }
        Apply(new ConverterSnapshot
        {
            Rows = rows,
            StartedAt = run?.StartedAt,
            FinishedAt = finished,
            Folder = run?.Folder,
            Fraction = overall.Fraction,
            Remaining = overall.Remaining,
            Now = _time.GetLocalNow(),
        });
    }

    /// <summary>Показывает снимок: экран, строки, итог. Кадры для отрисовки подаются сюда напрямую.</summary>
    public void Apply(ConverterSnapshot s)
    {
        _last = s;
        foreach (var r in s.Rows.Where(r => r.State == AgrRowState.Running)) _rowStarted.TryAdd(r.Id, s.Now);
        SyncRows(s.Rows);

        int total = s.Rows.Count;
        int done = s.Rows.Count(r => r.State == AgrRowState.Done);
        int failed = s.Rows.Count(r => r.State == AgrRowState.Error);
        int cancelled = s.Rows.Count(r => r.State == AgrRowState.Cancelled);
        int running = s.Rows.Count(r => r.State == AgrRowState.Running);
        int queued = s.Rows.Count(r => r.State == AgrRowState.Queued);
        Screen = total == 0 ? ConverterScreen.Intake : running + queued > 0 ? ConverterScreen.Running : ConverterScreen.Finished;

        OutputRootText = ConverterText.ShortPath(_service.OutputRoot);
        ParallelHint = $"Конвертация начинается сразу после добавления. Части обрабатываются {ConverterText.ByN(_service.Parallelism)}, остальные ждут в очереди.";

        ProgressTitle = ConverterText.ProgressTitle(total, done, running, failed, cancelled, queued);
        OverallPercent = (int)Math.Floor(Math.Clamp(s.Fraction, 0, 1) * 100);
        TimeSpan elapsed = s.StartedAt is DateTimeOffset st ? (s.FinishedAt ?? s.Now) - st : TimeSpan.Zero;
        OverallMeta = $"{OverallPercent} % · прошло {ConverterText.Clock(elapsed)} · осталось " +
                      (s.Remaining is TimeSpan rem ? ConverterText.ApproxTotal(rem) : "—");

        var summary = ConverterText.Summary(total, done, failed, cancelled, checkWhenAnyDone: true);
        SummaryDoneText = summary.DoneText;
        SummarySeparator = summary.Separator;
        SummaryErrorText = summary.ErrorText;
        SummaryDoneColor = summary.DoneColor;

        long resultBytes = s.Rows.Where(r => r.State == AgrRowState.Done).Sum(r => r.ZipBytes);
        var meta = new List<string>();
        if (s.StartedAt is DateTimeOffset started)
        {
            string span = started.ToString("dd.MM.yyyy, HH:mm", CultureInfo.InvariantCulture);
            if (s.FinishedAt is DateTimeOffset fin) span += "–" + fin.ToString("HH:mm", CultureInfo.InvariantCulture);
            meta.Add(span);
            meta.Add(ConverterText.Human(elapsed));
        }
        if (done > 0) meta.Add("результаты " + ConverterText.Total(resultBytes));
        if (!string.IsNullOrEmpty(s.Folder)) meta.Add(ConverterText.ShortPath(s.Folder));
        FinishedMeta = string.Join(" · ", meta);

        string example = s.Rows.FirstOrDefault(r => r.State == AgrRowState.Done)?.Name ?? s.Rows.FirstOrDefault()?.Name ?? "часть";
        StudioHint = $"В браузере: проект → модель → «Загрузить версию», затем выберите файл части, например {example}{AgrConverterPaths.OutputSuffix}. Одна часть — одна модель.";
        ResultNote = $"Результат каждой части — файл {NamePattern(s.Rows.Select(r => r.Name).ToList())}{AgrConverterPaths.OutputSuffix}, пакет GLB для Студии.";

        OnPropertyChanged(string.Empty);
        CancelAllCommand.RaiseCanExecuteChanged();
        OpenResultsCommand.RaiseCanExecuteChanged();
        NewRunCommand.RaiseCanExecuteChanged();
    }

    /// <summary>«SM_ProektiruemyjProezd_NNN» — общее начало имён частей до последнего «_».</summary>
    static string NamePattern(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return "<часть>";
        if (names.Count == 1) return names[0];
        string prefix = names.Aggregate((a, b) =>
        {
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            return a[..i];
        });
        int cut = prefix.LastIndexOf('_');
        return cut > 0 ? prefix[..(cut + 1)] + "NNN" : "<часть>";
    }

    void SyncRows(IReadOnlyList<ConverterRowSnapshot> rows)
    {
        var ids = rows.Select(r => r.Id).ToHashSet();
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Rows[i].Id)) Rows.RemoveAt(i);
        }
        for (int i = 0; i < rows.Count; i++)
        {
            var existing = i < Rows.Count && Rows[i].Id == rows[i].Id ? Rows[i] : Rows.FirstOrDefault(r => r.Id == rows[i].Id);
            if (existing == null)
            {
                Rows.Insert(Math.Min(i, Rows.Count), new ConverterRowViewModel(rows[i], this));
                continue;
            }
            if (!ReferenceEquals(existing.Snapshot, rows[i]) && existing.Snapshot != rows[i]) existing.Apply(rows[i]);
            int at = Rows.IndexOf(existing);
            if (at != i) Rows.Move(at, i);
        }
    }

    /// <summary>Перечитывает историю (в потоке окна).</summary>
    public void RefreshHistory() => ApplyHistory(_service.History.Runs);

    public void ApplyHistory(IReadOnlyList<AgrHistoryRun> runs)
    {
        History.Clear();
        foreach (var run in runs) History.Add(new ConverterHistoryItemViewModel(run, this));
        OnPropertyChanged(nameof(HistoryEmpty));
    }

    void ChangeOutputRoot()
    {
        string? root = _ui.PickOutputRoot(_service.OutputRoot);
        if (string.IsNullOrWhiteSpace(root)) return;
        _service.SetOutputRoot(root);
        Refresh();
    }

    void OpenResults()
    {
        if (!string.IsNullOrEmpty(_last.Folder)) _ui.OpenFolder(_last.Folder!);
    }

    void NewRun()
    {
        if (_service.NewRun()) Refresh();
    }

    // ---- действия строк ----

    public void CancelRow(Guid id)
    {
        _service.Cancel(id);
        Refresh();
    }

    public void ShowInFolder(ConverterRowViewModel row)
    {
        if (!string.IsNullOrEmpty(row.Snapshot.ZipPath)) _ui.ShowInFolder(row.Snapshot.ZipPath!);
    }

    public void ShowReport(ConverterRowViewModel row)
    {
        var source = _service.Rows.FirstOrDefault(r => r.Id == row.Id);
        AgrPartReport report = source != null
            ? AgrConverterService.LoadReport(source)
            : AgrPartReport.Failure(row.Name, row.Snapshot.SourcePath, row.Snapshot.State, row.Snapshot.Error ?? "строки уже нет", row.Snapshot.FailedStage, null);
        DateTimeOffset? started = _rowStarted.TryGetValue(row.Id, out var t) ? t : _last.StartedAt;
        _ui.ShowReport(ConverterReportViewModel.ForPart(report, _ui, started, row.Snapshot.Elapsed, row.Snapshot.SourceBytes));
    }

    public void CopyPath(ConverterRowViewModel row)
    {
        if (!string.IsNullOrEmpty(row.Snapshot.ZipPath)) _ui.CopyText(row.Snapshot.ZipPath!);
    }

    public void OpenRunFolder(ConverterHistoryItemViewModel item)
    {
        if (!string.IsNullOrWhiteSpace(item.Run.Folder)) _ui.OpenFolder(item.Run.Folder);
    }

    public void ShowRunReport(ConverterHistoryItemViewModel item) => _ui.ShowReport(ConverterReportViewModel.ForRun(item.Run, _ui));

    /// <summary>«Убрать из истории»: только запись; файлы результатов остаются в папке.</summary>
    public void RemoveFromHistory(ConverterHistoryItemViewModel item)
    {
        _service.History.Remove(item.Id);
        RefreshHistory();
    }

    // ---- события службы (рабочие потоки) ----

    void ServiceLog(string line) => _post(() =>
    {
        if (_log != null) _log(line);
        else _pendingLog.Add(line);
    });

    void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    void OnHistoryChanged(object? sender, EventArgs e) => _post(() =>
    {
        if (!_disposed) RefreshHistory();
    });

    /// <summary>
    /// Закрытие окна: отписаться, отменить все части и подождать их запись в историю не дольше
    /// <see cref="DefaultShutdownWait"/> (окно уже закрыто; дольше не держим — незаписанный запуск при следующем старте
    /// станет «Прервано»).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.RowChanged -= _onRow;
        _service.RowsChanged -= _onRows;
        _service.RunFinished -= _onRun;
        _service.History.Changed -= OnHistoryChanged;
        _service.Shutdown(_shutdownWait);
    }
}

/// <summary>Перевод в поток окна: только асинхронно (<c>BeginInvoke</c>), см. <see cref="ConverterViewModel"/>.</summary>
public static class ConverterDispatch
{
    public static Action<Action> Post(System.Windows.Threading.Dispatcher dispatcher) => action => dispatcher.BeginInvoke(action);
}
