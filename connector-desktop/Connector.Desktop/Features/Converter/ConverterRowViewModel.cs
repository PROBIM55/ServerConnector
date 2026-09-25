using System.Globalization;
using System.IO;
using Connector.AgrConversion;
using Connector.AgrConversion.Service;
using Connector.Desktop.Mvvm;

namespace Connector.Desktop.Features.Converter;

public enum ConverterRowKind
{
    Queued,
    Running,
    Done,
    Error,
    Cancelled,
}

/// <summary>
/// Снимок строки службы для экрана. В приложении строится в потоке окна из <see cref="AgrConversionRow"/>; для
/// отрисовки кадров без службы — напрямую.
/// </summary>
public sealed record ConverterRowSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public string SourcePath { get; init; } = "";
    public long SourceBytes { get; init; }
    public AgrRowState State { get; init; }
    public AgrConvertPhase? Phase { get; init; }
    public double Fraction { get; init; }
    public string? Error { get; init; }
    public string? FailedStage { get; init; }
    public bool CancelRequested { get; init; }
    public string? ZipPath { get; init; }
    public long ZipBytes { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan? Remaining { get; init; }

    public static ConverterRowSnapshot Of(AgrConversionRow r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        SourcePath = r.SourcePath,
        SourceBytes = r.SourceBytes,
        State = r.State,
        Phase = r.Phase,
        Fraction = r.Fraction,
        Error = r.Error,
        FailedStage = r.FailedStage,
        CancelRequested = r.CancelRequested,
        ZipPath = r.ZipPath,
        ZipBytes = r.ZipBytes,
        Elapsed = r.Elapsed,
        Remaining = r.Remaining,
    };
}

/// <summary>Действия строк и записей истории — их выполняет вид-модель вкладки.</summary>
public interface IConverterRowActions
{
    void CancelRow(Guid id);
    void ShowInFolder(ConverterRowViewModel row);
    void ShowReport(ConverterRowViewModel row);
    void CopyPath(ConverterRowViewModel row);
    void OpenRunFolder(ConverterHistoryItemViewModel item);
    void ShowRunReport(ConverterHistoryItemViewModel item);
    void RemoveFromHistory(ConverterHistoryItemViewModel item);
}

/// <summary>Строка таблицы частей (кадры 2 и 3): этап, пять точек, доля, время, ошибка, действия.</summary>
public sealed class ConverterRowViewModel : ObservableObject
{
    const string InputStage = "проверка ввода"; // AgrConversionRow.MarkError до начала конвертации
    ConverterRowSnapshot _s = null!;

    public ConverterRowViewModel(ConverterRowSnapshot snapshot, IConverterRowActions actions)
    {
        CancelCommand = new RelayCommand(() => actions.CancelRow(Id), () => CanCancel);
        OpenFolderCommand = new RelayCommand(() => actions.ShowInFolder(this), () => Kind == ConverterRowKind.Done);
        ReportCommand = new RelayCommand(() => actions.ShowReport(this), () => Kind is ConverterRowKind.Done or ConverterRowKind.Error or ConverterRowKind.Cancelled);
        CopyPathCommand = new RelayCommand(() => actions.CopyPath(this), () => Kind == ConverterRowKind.Done);
        Apply(snapshot);
    }

    public ConverterRowSnapshot Snapshot => _s;
    public Guid Id => _s.Id;
    public string Name => _s.Name;

    public ConverterRowKind Kind { get; private set; }
    public string SourceSizeText { get; private set; } = "";
    public bool CanCancel { get; private set; }

    // Кадр 2: этап, точки, прогресс, время.
    public IReadOnlyList<string> Dots { get; private set; } = Array.Empty<string>();
    public bool ShowDots { get; private set; }
    public string StageLabel { get; private set; } = "";
    public string StageColor { get; private set; } = ConverterColors.Text;
    public string CellColor { get; private set; } = ConverterColors.Text;
    public bool ShowBar { get; private set; }
    public bool ShowDash { get; private set; }
    public int PercentValue { get; private set; }
    public string PercentText { get; private set; } = "";
    public string ElapsedText { get; private set; } = "";
    public string RemainingText { get; private set; } = "";
    public bool IsError => Kind == ConverterRowKind.Error;
    public bool IsNotError => Kind != ConverterRowKind.Error;
    public string ErrorLong { get; private set; } = "";

    // Кадр 3: результат, исход, действия.
    public string ResultText { get; private set; } = "";
    public bool ShowResultActions => Kind == ConverterRowKind.Done;
    public bool ShowOutcome => Kind is ConverterRowKind.Error or ConverterRowKind.Cancelled or ConverterRowKind.Queued or ConverterRowKind.Running;
    public string OutcomeText { get; private set; } = "";
    public string OutcomeColor { get; private set; } = ConverterColors.Dim;

    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ReportCommand { get; }
    public RelayCommand CopyPathCommand { get; }

    /// <summary>Новое состояние строки (в потоке окна). Все привязки строки обновляются разом.</summary>
    public void Apply(ConverterRowSnapshot s)
    {
        _s = s;
        Kind = s.State switch
        {
            AgrRowState.Running => ConverterRowKind.Running,
            AgrRowState.Done => ConverterRowKind.Done,
            AgrRowState.Error => ConverterRowKind.Error,
            AgrRowState.Cancelled => ConverterRowKind.Cancelled,
            _ => ConverterRowKind.Queued,
        };
        bool active = Kind is ConverterRowKind.Queued or ConverterRowKind.Running;
        SourceSizeText = s.SourceBytes > 0 ? ConverterText.Mb(s.SourceBytes) : "—";
        CanCancel = active && !s.CancelRequested;

        int current = s.Phase is AgrConvertPhase p ? (int)p : 0;
        Dots = Enumerable.Range(0, AgrConversionStages.All.Count)
            .Select(i => Kind != ConverterRowKind.Running ? "off" : i < current ? "on" : i == current ? "cur" : "off")
            .ToArray();
        ShowDots = Kind == ConverterRowKind.Running && !s.CancelRequested;
        StageLabel = Kind switch
        {
            _ when active && s.CancelRequested => "Отмена…",
            ConverterRowKind.Queued => "В очереди",
            ConverterRowKind.Running => ConverterText.Capitalize(ConverterText.PhaseLabel(s.Phase ?? AgrConvertPhase.Read)),
            ConverterRowKind.Done => "✓ Готово · " + ConverterText.Mb(s.ZipBytes),
            ConverterRowKind.Cancelled => "Отменено",
            _ => "Ошибка",
        };
        CellColor = Kind is ConverterRowKind.Queued or ConverterRowKind.Cancelled ? ConverterColors.Dim : ConverterColors.Text;
        StageColor = Kind == ConverterRowKind.Done ? ConverterColors.Ok : CellColor;

        ShowBar = Kind is ConverterRowKind.Running or ConverterRowKind.Done;
        ShowDash = !ShowBar && Kind != ConverterRowKind.Error;
        PercentValue = Kind == ConverterRowKind.Done ? 100 : (int)Math.Floor(Math.Clamp(s.Fraction, 0, 1) * 100);
        PercentText = PercentValue.ToString(CultureInfo.InvariantCulture) + " %";
        ElapsedText = Kind == ConverterRowKind.Queued ? "—" : ConverterText.Clock(s.Elapsed);
        RemainingText = Kind == ConverterRowKind.Running && !s.CancelRequested && s.Remaining is TimeSpan r ? "≈ " + ConverterText.Clock(r) : "—";

        string error = (s.Error ?? "ошибка").TrimEnd('.');
        bool inputCheck = s.Phase == null && s.FailedStage == InputStage;
        ErrorLong = Kind != ConverterRowKind.Error ? ""
            : s.Phase is AgrConvertPhase ph ? $"Ошибка на этапе «{ConverterText.PhaseLabel(ph)}»: {error}."
            : inputCheck ? $"Ошибка при проверке архива: {error}. Проверьте, что это zip части АГР и он скачан полностью."
            : $"Ошибка: {error}.";

        ResultText = Kind == ConverterRowKind.Done ? ConverterText.Mb(s.ZipBytes) : "";
        OutcomeText = Kind switch
        {
            ConverterRowKind.Error => inputCheck ? $"Ошибка: {error}. Проверьте, что это zip части АГР и он скачан полностью." : $"Ошибка: {error}.",
            ConverterRowKind.Cancelled => "Отменено — результата нет.",
            ConverterRowKind.Done => "",
            _ => StageLabel,
        };
        OutcomeColor = Kind == ConverterRowKind.Error ? ConverterColors.Warn : ConverterColors.Dim;

        OnPropertyChanged(string.Empty);
        CancelCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        ReportCommand.RaiseCanExecuteChanged();
        CopyPathCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>Запись истории (кадр 5): дата, число частей, итог, папка, действия.</summary>
public sealed class ConverterHistoryItemViewModel : ObservableObject
{
    public ConverterHistoryItemViewModel(AgrHistoryRun run, IConverterRowActions actions)
    {
        Run = run;
        DateText = run.StartedAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        PartsText = run.Total.ToString(CultureInfo.InvariantCulture);
        Summary = ConverterText.RunSummary(run);
        FolderText = ConverterText.RunFolderShort(run.Folder);
        FolderExists = !string.IsNullOrWhiteSpace(run.Folder) && Directory.Exists(run.Folder);
        // Идущий запуск этого сеанса не убирается; прерванный прошлым закрытием Коннектора — убирается (ревью C2b).
        CanRemove = run.FinishedAt != null || run.Interrupted;
        OpenFolderCommand = new RelayCommand(() => actions.OpenRunFolder(this), () => FolderExists);
        ReportCommand = new RelayCommand(() => actions.ShowRunReport(this));
        RemoveCommand = new RelayCommand(() => actions.RemoveFromHistory(this), () => CanRemove);
    }

    public AgrHistoryRun Run { get; }
    public Guid Id => Run.Id;
    public string DateText { get; }
    public string PartsText { get; }
    public ConverterSummary Summary { get; }
    public string SummaryDoneText => Summary.DoneText;
    public string SummarySeparator => Summary.Separator;
    public string SummaryErrorText => Summary.ErrorText;
    public string SummaryDoneColor => Summary.DoneColor;
    public string FolderText { get; }
    public bool FolderExists { get; }
    public bool CanRemove { get; }

    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ReportCommand { get; }
    public RelayCommand RemoveCommand { get; }
}
