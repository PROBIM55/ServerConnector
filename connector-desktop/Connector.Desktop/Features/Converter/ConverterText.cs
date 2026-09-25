using System.Globalization;
using System.IO;
using Connector.AgrConversion;
using Connector.AgrConversion.Service;

namespace Connector.Desktop.Features.Converter;

/// <summary>Цвета статуса приложения (VpnViewModel, StandardView): строкой — вид-модель не зависит от WPF.</summary>
public static class ConverterColors
{
    public const string Text = "#F4F7FB";
    public const string Ok = "#00FA9A";    // Brushes.MediumSpringGreen — успех
    public const string Warn = "#FFA500";  // Brushes.Orange — проблема
    public const string Dim = "#A9A9A9";   // Brushes.DarkGray — нейтрально
}

/// <summary>Итог словами для экрана: зелёная часть «Готово N из M», оранжевая «K ошибок», запятая между ними.</summary>
public sealed record ConverterSummary(string DoneText, string Separator, string ErrorText, string DoneColor);

/// <summary>Тексты вкладки «Конвертер» (кадры 1–5 макета C2). Чистые функции — проверяются без окна.</summary>
public static class ConverterText
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public const string StagesLegend = "Этапы: распаковка → чтение → текстуры → уровни детализации → упаковка";

    public static string Plural(int n, string one, string few, string many) => AgrConverterText.Plural(n, one, few, many);

    public static string Errors(int n) => $"{n} {Plural(n, "ошибка", "ошибки", "ошибок")}";

    /// <summary>Этап строки словами макета (пять точек).</summary>
    public static string PhaseLabel(AgrConvertPhase phase) => phase switch
    {
        AgrConvertPhase.Read => "распаковка",
        AgrConvertPhase.Geometry => "чтение",
        AgrConvertPhase.Textures => "текстуры",
        AgrConvertPhase.Levels => "уровни детализации",
        AgrConvertPhase.Pack => "упаковка",
        _ => phase.ToString(),
    };

    public static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], Ru) + s[1..];

    /// <summary>«225,0 МБ» — размер архива и результата (десятичные МБ, как в макете).</summary>
    public static string Mb(long bytes) => (Math.Max(0, bytes) / 1e6).ToString("0.0", Ru) + " МБ";

    /// <summary>«0,53 МБ» — размер уровня в отчёте.</summary>
    public static string Mb2(long bytes) => (Math.Max(0, bytes) / 1e6).ToString("0.00", Ru) + " МБ";

    /// <summary>Сумма результатов: «480 МБ», «4,4 ГБ», «23,2 МБ».</summary>
    public static string Total(long bytes) =>
        bytes >= 1_000_000_000 ? (bytes / 1e9).ToString("0.0", Ru) + " ГБ"
        : bytes >= 100_000_000 ? (bytes / 1e6).ToString("0", Ru) + " МБ"
        : Mb(bytes);

    /// <summary>«1:46», «1:02:03».</summary>
    public static string Clock(TimeSpan t) => AgrConverterText.Duration(t < TimeSpan.Zero ? TimeSpan.Zero : t);

    /// <summary>«46 с», «17 мин 48 с», «1 ч 5 мин».</summary>
    public static string Human(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} ч {t.Minutes} мин";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} мин {t.Seconds} с";
        return $"{(int)Math.Round(t.TotalSeconds)} с";
    }

    /// <summary>Общая оценка «осталось»: «≈ 13 мин», «≈ 40 с», «≈ 1 ч 5 мин».</summary>
    public static string ApproxTotal(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"≈ {(int)t.TotalHours} ч {t.Minutes} мин";
        if (t.TotalSeconds >= 60) return $"≈ {(int)Math.Ceiling(t.TotalMinutes)} мин";
        return $"≈ {(int)Math.Round(t.TotalSeconds)} с";
    }

    /// <summary>«по две» — сколько частей идёт одновременно.</summary>
    public static string ByN(int n) => n switch
    {
        1 => "по одной",
        2 => "по две",
        3 => "по три",
        4 => "по четыре",
        _ => $"по {n}",
    };

    /// <summary>Путь с понятными именами папок: «Документы\Structura\Конвертер», «Загрузки\…».</summary>
    public static string ShortPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "—";
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var known = new (string Dir, string Name)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Документы"),
            (string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, "Downloads"), "Загрузки"),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Рабочий стол"),
        };
        foreach (var (dir, name) in known)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string d = dir.TrimEnd('\\', '/');
            if (string.Equals(path, d, StringComparison.OrdinalIgnoreCase)) return name;
            if (path.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)) return name + path[d.Length..];
        }
        return path;
    }

    /// <summary>Папка запуска в истории: «Конвертер\2026-09-24_22-40».</summary>
    public static string RunFolderShort(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "—";
        string trimmed = folder.TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed);
        string? parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? "");
        return string.IsNullOrEmpty(parent) ? name : parent + "\\" + name;
    }

    /// <summary>Строка шага над общей полосой: «Идёт конвертация: готово 3 из 16, в работе 2, ошибка 1, в очереди 10».</summary>
    public static string ProgressTitle(int total, int done, int running, int failed, int cancelled, int queued)
    {
        var s = $"Идёт конвертация: готово {done} из {total}";
        if (running > 0) s += $", в работе {running}";
        if (failed > 0) s += $", {Plural(failed, "ошибка", "ошибки", "ошибок")} {failed}";
        if (cancelled > 0) s += $", отменено {cancelled}";
        if (queued > 0) s += $", в очереди {queued}";
        return s;
    }

    /// <summary>
    /// Итог запуска. Отменённые — не готовые: запуск с отменой — «Отменено: готово N из M». Галочка у итога экрана
    /// (кадр 3) — когда есть готовые и нет отмены; у записи истории (кадр 5) — только когда готово всё.
    /// </summary>
    public static ConverterSummary Summary(int total, int done, int failed, int cancelled, bool checkWhenAnyDone)
    {
        string err = failed > 0 ? Errors(failed) : "";
        string sep = failed > 0 ? ", " : "";
        if (cancelled > 0)
        {
            return new ConverterSummary($"Отменено: готово {done} из {total}", sep, err, ConverterColors.Dim);
        }
        bool check = done > 0 && (checkWhenAnyDone || (failed == 0 && done == total));
        return new ConverterSummary((check ? "✓ " : "") + $"Готово {done} из {total}", sep, err, done > 0 ? ConverterColors.Ok : ConverterColors.Dim);
    }

    /// <summary>
    /// Итог записи истории (кадр 5, отчёт запуска): идёт — «Идёт: готово N из M»; прерван закрытием Коннектора —
    /// «Прервано: готово N из M» (ревью C2b); закончен — <see cref="Summary"/>.
    /// </summary>
    public static ConverterSummary RunSummary(AgrHistoryRun run)
    {
        if (run.Interrupted)
        {
            return new ConverterSummary($"Прервано: готово {run.Done} из {run.Total}", run.Failed > 0 ? ", " : "",
                                        run.Failed > 0 ? Errors(run.Failed) : "", ConverterColors.Dim);
        }
        if (run.FinishedAt == null) return new ConverterSummary($"Идёт: готово {run.Done} из {run.Total}", "", "", ConverterColors.Text);
        return Summary(run.Total, run.Done, run.Failed, run.Cancelled, checkWhenAnyDone: false);
    }

    /// <summary>Число в отчёте: «76 425».</summary>
    public static string Count(long n) => n.ToString("#,0", Ru);

    /// <summary>Сантиметры отчёта: «12», «1,7».</summary>
    public static string Cm(double v) => v.ToString(Math.Abs(v) >= 10 ? "0" : "0.#", Ru);
}
