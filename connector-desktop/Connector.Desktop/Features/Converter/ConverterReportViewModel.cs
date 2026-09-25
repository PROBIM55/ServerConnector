using System.Globalization;
using System.IO;
using System.Text;
using Connector.AgrConversion.Service;
using Connector.Desktop.Mvvm;

namespace Connector.Desktop.Features.Converter;

/// <summary>Строка таблицы «Треугольники и уровни детализации» (кадр 4).</summary>
public sealed record ConverterReportLevelRow(string Level, string Triangles, string Share, string Textures, string Size,
                                             string Accuracy, string AccuracyColor);

/// <summary>Строка «Файлы»: подпись и путь.</summary>
public sealed record ConverterReportFile(string Key, string Value);

/// <summary>Часть в отчёте запуска (история → «Отчёт»): итог части и её собственный отчёт.</summary>
public sealed class ConverterReportPartRow
{
    public ConverterReportPartRow(AgrHistoryPart part, Action<AgrHistoryPart> open)
    {
        Name = part.Name;
        (Outcome, OutcomeColor) = part.State switch
        {
            AgrRowState.Done => ("✓ Готово · " + ConverterText.Mb(part.ZipBytes), ConverterColors.Ok),
            AgrRowState.Error => ("Ошибка: " + (part.Error ?? "результата нет").TrimEnd('.'), ConverterColors.Warn),
            AgrRowState.Cancelled => ("Отменено — результата нет", ConverterColors.Dim),
            _ => ("Не закончена", ConverterColors.Dim),
        };
        ReportCommand = new RelayCommand(() => open(part));
    }

    public string Name { get; }
    public string Outcome { get; }
    public string OutcomeColor { get; }
    public RelayCommand ReportCommand { get; }
}

/// <summary>
/// Окно «Отчёт» (кадр 4, по образцу «Что нового»): части — уровни, текстуры, предупреждения, файлы; части с ошибкой —
/// этап и текст ошибки; запуска из истории — список частей с итогом и отчётом каждой.
/// </summary>
public sealed class ConverterReportViewModel
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    readonly IConverterUi _ui;
    readonly string? _folder;
    readonly string? _selectFile;

    ConverterReportViewModel(IConverterUi ui, string title, string? folder, string? selectFile)
    {
        _ui = ui;
        Title = title;
        _folder = folder;
        _selectFile = selectFile;
        CopyCommand = new RelayCommand(() => _ui.CopyText(PlainText));
        OpenFolderCommand = new RelayCommand(OpenFolder, () => CanOpenFolder);
    }

    public string Title { get; }
    public string WindowTitle => "Отчёт — " + Title;
    public string Meta { get; private init; } = "";
    public bool IsFailure { get; private init; }
    public bool IsRun { get; private init; }
    public bool IsPart => !IsFailure && !IsRun;
    public string ErrorStage { get; private init; } = "";
    public string ErrorText { get; private init; } = "";
    public IReadOnlyList<ConverterReportLevelRow> Levels { get; private init; } = Array.Empty<ConverterReportLevelRow>();
    public IReadOnlyList<string> Textures { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; private init; } = Array.Empty<string>();
    public bool HasWarnings => Warnings.Count > 0;
    public bool NoWarnings => Warnings.Count == 0;
    public IReadOnlyList<ConverterReportFile> Files { get; private init; } = Array.Empty<ConverterReportFile>();
    public IReadOnlyList<ConverterReportPartRow> Parts { get; private init; } = Array.Empty<ConverterReportPartRow>();
    public bool CanOpenFolder => (_selectFile != null && File.Exists(_selectFile)) || (_folder != null && Directory.Exists(_folder));

    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    void OpenFolder()
    {
        if (_selectFile != null && File.Exists(_selectFile)) _ui.ShowInFolder(_selectFile);
        else if (_folder != null) _ui.OpenFolder(_folder);
    }

    /// <summary>Отчёт части: готовой (уровни, текстуры, предупреждения) или с ошибкой (этап и текст).</summary>
    public static ConverterReportViewModel ForPart(AgrPartReport r, IConverterUi ui, DateTimeOffset? started = null,
                                                   TimeSpan? duration = null, long? sourceBytes = null)
    {
        string? folder = r.Folder ?? (r.SourcePath != null ? Path.GetDirectoryName(r.SourcePath) : null);
        long? srcBytes = r.SourceBytes ?? sourceBytes;
        var files = new List<ConverterReportFile>();
        string? source = r.SourcePath ?? r.SourceName;
        if (source != null) files.Add(new("Исходник", ConverterText.ShortPath(source) + (srcBytes is long sb && sb > 0 ? " · " + ConverterText.Mb(sb) : "")));
        if (r.ZipPath != null && File.Exists(r.ZipPath)) files.Add(new("Результат", ConverterText.ShortPath(r.ZipPath)));
        if (r.ReportPath != null && File.Exists(r.ReportPath)) files.Add(new("Отчёт", ConverterText.ShortPath(r.ReportPath)));

        if (r.IsFailure)
        {
            string stage = string.IsNullOrWhiteSpace(r.FailedStage) ? "не известен" : r.FailedStage!;
            string state = r.State switch
            {
                AgrRowState.Cancelled => "Отменено",
                AgrRowState.Done => "Готово, но отчёт не прочитан",
                _ => "Ошибка",
            };
            var meta = new List<string> { state };
            if (started is DateTimeOffset s) meta.Add("конвертация " + s.ToString("dd.MM.yyyy, HH:mm", Inv));
            if (r.Percent is int p) meta.Add($"выполнено {p} %");
            return new ConverterReportViewModel(ui, r.Part, folder, r.ZipPath)
            {
                IsFailure = true,
                Meta = string.Join(" · ", meta),
                ErrorStage = "Этап: " + stage,
                ErrorText = ConverterText.Capitalize((r.Error ?? "результата нет").TrimEnd('.')) + ".",
                Files = files,
            };
        }

        long zipBytes = r.ZipPath != null && File.Exists(r.ZipPath) ? new FileInfo(r.ZipPath).Length : r.Levels.Sum(l => l.Bytes);
        var metaParts = new List<string>();
        if (started is DateTimeOffset st) metaParts.Add("Конвертация " + st.ToString("dd.MM.yyyy, HH:mm", Inv));
        if (duration is TimeSpan d && d > TimeSpan.Zero) metaParts.Add(ConverterText.Human(d));
        string zipName = r.ZipPath != null ? Path.GetFileName(r.ZipPath) : r.Part + AgrConverterPaths.OutputSuffix;
        metaParts.Add($"результат {zipName} — {ConverterText.Mb(zipBytes)}, пакет GLB для Студии");
        string metaText = string.Join(" · ", metaParts);
        if (metaParts.Count == 1) metaText = ConverterText.Capitalize(metaText);

        return new ConverterReportViewModel(ui, r.Part, folder, r.ZipPath)
        {
            Meta = metaText,
            Levels = LevelRows(r),
            Textures = TextureLines(r),
            Warnings = r.Warnings.Concat(r.Dropped.Select(x => "Не переносится: " + x)).ToList(),
            Files = files,
        };
    }

    /// <summary>Отчёт запуска из истории: итог по каждой части и её отчёт.</summary>
    public static ConverterReportViewModel ForRun(AgrHistoryRun run, IConverterUi ui)
    {
        var summary = ConverterText.RunSummary(run);
        string when = run.StartedAt.ToString("dd.MM.yyyy, HH:mm", Inv) + (run.FinishedAt is DateTimeOffset f ? "–" + f.ToString("HH:mm", Inv) : "");
        string meta = $"{when} · {summary.DoneText}{summary.Separator}{summary.ErrorText} · {ConverterText.ShortPath(run.Folder)}";
        var vm = new ConverterReportViewModel(ui, "Запуск " + run.StartedAt.ToString("dd.MM.yyyy HH:mm", Inv), run.Folder, null)
        {
            IsRun = true,
            Meta = meta,
            Parts = run.Parts.Select(p => new ConverterReportPartRow(p, part =>
                ui.ShowReport(ForPart(AgrConverterService.LoadReport(part), ui, run.StartedAt,
                                      part.Seconds > 0 ? TimeSpan.FromSeconds(part.Seconds) : null)))).ToList(),
            Files = new[] { new ConverterReportFile("Папка", ConverterText.ShortPath(run.Folder)) },
        };
        return vm;
    }

    static string LevelName(string name) => name switch
    {
        "L0" => "L0 — издалека",
        "L1" => "L1 — средний",
        "L2" => "L2 — вблизи",
        _ => name,
    };

    static IReadOnlyList<ConverterReportLevelRow> LevelRows(AgrPartReport r)
    {
        var rows = new List<ConverterReportLevelRow>
        {
            new("Исходник (FBX)", r.TrianglesSource > 0 ? ConverterText.Count(r.TrianglesSource) : "—", r.TrianglesSource > 0 ? "100 %" : "—",
                "—", "—", "—", ConverterColors.Dim),
        };
        foreach (var l in r.Levels)
        {
            string share = r.TrianglesSource > 0 ? Math.Round(100.0 * l.Triangles / r.TrianglesSource).ToString("0", Inv) + " %" : "—";
            string tex = l.ImageMaxPx > 0 ? $"{l.ImageMaxPx}²" : "—";
            string acc;
            string color = ConverterColors.Text;
            if (!l.Simplified)
            {
                acc = l.SimplifySkipped != null
                    ? "не упрощался (малая часть); координаты округлены ≤ 1 см"
                    : "не упрощался; координаты округлены ≤ 1 см";
            }
            else
            {
                acc = $"99 % точек ближе {ConverterText.Cm(l.ErrorP99Cm)} см, наибольшее {ConverterText.Cm(l.ErrorMaxCm)} см";
                if (l.FallbackGeometryOf != null)
                {
                    acc += $"; геометрия {l.FallbackGeometryOf} (simplify.fallback)";
                    color = ConverterColors.Warn;
                }
                if (l.LimitExceeded) color = ConverterColors.Warn;
            }
            rows.Add(new(LevelName(l.Name), ConverterText.Count(l.Triangles), share, tex, ConverterText.Mb2(l.Bytes), acc, color));
        }
        return rows;
    }

    static IReadOnlyList<string> TextureLines(AgrPartReport r)
    {
        var lines = new List<string>();
        if (r.TextureFiles is int files) lines.Add($"В архиве {files} {ConverterText.Plural(files, "файл", "файла", "файлов")} текстур.");
        if (r.TexturesReal is int real) lines.Add($"Использовано {real} {ConverterText.Plural(real, "текстура", "текстуры", "текстур")}: в пакете они уменьшены до размера уровня и сжаты.");
        if (r.TextureStubs is int stubs && stubs > 0)
        {
            lines.Add($"Отброшено {stubs} {ConverterText.Plural(stubs, "однотонная заглушка", "однотонные заглушки", "однотонных заглушек")}: вместо них используется цвет материала.");
        }
        if (r.TexturesUnused is int unused && unused > 0)
        {
            lines.Add($"Не привязаны к граням и не переносятся: {unused}.");
        }
        if (lines.Count == 0) lines.Add("Сведений о текстурах нет: отчёт чтения части не найден.");
        return lines;
    }

    /// <summary>Отчёт текстом — «Скопировать отчёт».</summary>
    public string PlainText
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine(Title);
            sb.AppendLine(Meta);
            if (IsFailure)
            {
                sb.AppendLine(ErrorStage);
                sb.AppendLine(ErrorText);
            }
            if (Levels.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Треугольники и уровни детализации");
                foreach (var l in Levels) sb.AppendLine($"  {l.Level}: {l.Triangles} ({l.Share}), текстуры {l.Textures}, {l.Size}, {l.Accuracy}");
            }
            if (IsPart)
            {
                sb.AppendLine();
                sb.AppendLine("Текстуры");
                foreach (var t in Textures) sb.AppendLine("  • " + t);
                sb.AppendLine();
                sb.AppendLine("Предупреждения");
                if (Warnings.Count == 0) sb.AppendLine("  нет");
                foreach (var w in Warnings) sb.AppendLine("  • " + w);
            }
            if (Parts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Части");
                foreach (var p in Parts) sb.AppendLine($"  {p.Name}: {p.Outcome}");
            }
            if (Files.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Файлы");
                foreach (var f in Files) sb.AppendLine($"  {f.Key}: {f.Value}");
            }
            return sb.ToString().TrimEnd();
        }
    }
}
