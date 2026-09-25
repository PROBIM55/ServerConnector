using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Connector.AgrConversion;
using Connector.AgrConversion.Service;
using Connector.Desktop.Features.Converter;
using Xunit;

namespace Connector.Desktop.Tests;

/// <summary>Конвертер-заглушка: каждая часть ждёт своего «отпустить», может упасть; zip результата с манифестом.</summary>
sealed class GateConverter : IAgrPartConverter
{
    readonly ConcurrentDictionary<string, ManualResetEventSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, Exception?>? Fail;

    ManualResetEventSlim Gate(string name) => _gates.GetOrAdd(name, _ => new ManualResetEventSlim(false));

    public void Release(string name) => Gate(name).Set();

    public void ReleaseAll()
    {
        _releaseAll = true;   // сначала флаг: строка, создавшая ворота позже обхода, увидит его сама
        foreach (var g in _gates.Values) g.Set();
    }

    volatile bool _releaseAll;

    /// <summary>Конвертер «не слышит» отмену (завис gltfpack): ждёт только свои ворота.</summary>
    public volatile bool IgnoreCancel;

    /// <summary>После отмены конвертер ещё столько занят (снятие gltfpack, уборка временных файлов), мс.</summary>
    public int CancelDelayMs;

    public AgrConvertResult Convert(string input, string outputDirectory, string workRoot, IProgress<AgrConvertProgress> progress,
                                    CancellationToken cancellationToken)
    {
        string name = Path.GetFileNameWithoutExtension(input);
        Directory.CreateDirectory(workRoot);
        progress.Report(new AgrConvertProgress("распаковка", 1, 10) { Phase = AgrConvertPhase.Read, PhaseFraction = 1 });
        progress.Report(new AgrConvertProgress("текстуры", 3, 10) { Phase = AgrConvertPhase.Textures, PhaseFraction = 0.5 });
        var gate = Gate(name);
        if (_releaseAll) gate.Set();
        if (IgnoreCancel) gate.Wait();
        else WaitHandle.WaitAny(new[] { gate.WaitHandle, cancellationToken.WaitHandle });
        if (cancellationToken.IsCancellationRequested && CancelDelayMs > 0) Thread.Sleep(CancelDelayMs);
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail?.Invoke(name) is { } ex) throw ex;
        progress.Report(new AgrConvertProgress("упаковка", 10, 10) { Phase = AgrConvertPhase.Pack });
        Directory.CreateDirectory(outputDirectory);
        string zipPath = Path.Combine(outputDirectory, name + ".glb.zip");
        var manifest = Manifest(name);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            w.Write(manifest.ToJsonString());
        }
        return new AgrConvertResult { PartName = name, ZipPath = zipPath, Manifest = manifest, PartReportJson = "{}" };
    }

    static JsonObject Manifest(string part) => new()
    {
        ["package"] = part,
        ["levels"] = new JsonObject { ["L0"] = "упрощение", ["L1"] = "упрощение", ["L2"] = "полная геометрия" },
        ["parts"] = new JsonArray(new JsonObject
        {
            ["part"] = part,
            ["triangles_ref"] = 1000,
            ["levels"] = new JsonObject
            {
                ["L0"] = new JsonObject { ["triangles"] = 250, ["bytes"] = 530_000, ["image_max_px"] = 256, ["err_fwd_p99_m"] = 0.12, ["err_fwd_max_m"] = 0.25,
                                          ["simplify"] = new JsonObject { ["error_m"] = 0.5 } },
                ["L1"] = new JsonObject { ["triangles"] = 600, ["bytes"] = 4_750_000, ["image_max_px"] = 1024, ["err_fwd_p99_m"] = 0.017, ["err_fwd_max_m"] = 0.073,
                                          ["simplify"] = new JsonObject { ["error_m"] = 0.1 } },
                ["L2"] = new JsonObject { ["triangles"] = 1000, ["bytes"] = 17_940_000, ["image_max_px"] = 2048 },
            },
        }),
    };
}

/// <summary>Действия окна — записываются, окна не открываются.</summary>
sealed class FakeUi : IConverterUi
{
    public readonly List<string> Opened = new();
    public readonly List<string> Selected = new();
    public readonly List<string> Urls = new();
    public readonly List<string> Copied = new();
    public readonly List<ConverterReportViewModel> Reports = new();
    public IReadOnlyList<string>? Zips { get; set; }
    public string? Folder { get; set; }
    public string? OutputRoot { get; set; }

    public IReadOnlyList<string>? PickZips() => Zips;
    public string? PickFolder() => Folder;
    public string? PickOutputRoot(string current) => OutputRoot;
    public void OpenFolder(string folder) => Opened.Add(folder);
    public void ShowInFolder(string file) => Selected.Add(file);
    public void OpenUrl(string url) => Urls.Add(url);
    public void CopyText(string text) => Copied.Add(text);
    public void ShowReport(ConverterReportViewModel report) => Reports.Add(report);
}

/// <summary>Среда: служба на заглушке, «поток окна» — очередь, которую тест прокачивает сам.</summary>
sealed class VmEnv : IDisposable
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "c2b-vm-" + Guid.NewGuid().ToString("N")[..8]);
    readonly ConcurrentQueue<Action> _posted = new();
    public readonly GateConverter Converter = new();
    public readonly FakeUi Ui = new();
    public readonly List<string> Log = new();
    public readonly List<int> LogThreads = new();
    public readonly ConverterViewModel Vm;
    public readonly int UiThread = Environment.CurrentManagedThreadId;

    public VmEnv(int parallel = 2, bool bindLog = true, TimeSpan? shutdownWait = null)
    {
        Directory.CreateDirectory(In);
        Vm = new ConverterViewModel(
            log => new AgrConverterService(Converter, new AgrConverterStorage(StorageDir),
                new AgrConverterServiceOptions { WorkRoot = Path.Combine(_dir, "work"), Log = log }),
            Ui,
            a => _posted.Enqueue(a), shutdownWait: shutdownWait);
        Vm.Service.SetOutputRoot(OutRoot);
        Vm.Service.SetParallelism(parallel);
        Vm.Refresh();
        if (bindLog) BindLog();
    }

    public string In => Path.Combine(_dir, "in");
    public string StorageDir => Path.Combine(_dir, "storage");
    public string OutRoot => Path.Combine(_dir, "out");

    public void BindLog() => Vm.Log = line => { Log.Add(line); LogThreads.Add(Environment.CurrentManagedThreadId); };

    public string Zip(string name)
    {
        string zp = Path.Combine(In, name + ".zip");
        using var z = ZipFile.Open(zp, ZipArchiveMode.Create);
        using var w = new StreamWriter(z.CreateEntry($"{name}/{name}.fbx").Open());
        w.Write("FBX");
        return zp;
    }

    public int Pump()
    {
        int n = 0;
        while (_posted.TryDequeue(out var a)) { a(); n++; }
        return n;
    }

    public void PumpUntil(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Pump();
            if (condition()) return;
            if (sw.Elapsed > Wait) throw new TimeoutException("не дождались: " + what);
            Thread.Sleep(5);
        }
    }

    public ConverterRowViewModel Row(string name) => Vm.Rows.Single(r => r.Name == name);

    public void Dispose()
    {
        Converter.ReleaseAll();
        try { Vm.Service.WaitIdleAsync().Wait(Wait); } catch (AggregateException) { }
        Vm.Dispose();
        Pump();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class ConverterViewModelTests
{
    [Fact]
    public void Intake_Defaults_ParallelTwo_OutputRoot()
    {
        using var e = new VmEnv();
        Assert.Equal(ConverterScreen.Intake, e.Vm.Screen);
        Assert.True(e.Vm.IsIntake);
        Assert.Empty(e.Vm.Rows);
        Assert.Contains("по две", e.Vm.ParallelHint);
        Assert.Equal(ConverterText.ShortPath(e.OutRoot), e.Vm.OutputRootText);
        Assert.Equal(2, AgrConverterConstants.DefaultParallelism);
    }

    [Fact]
    public void Add_ShowsRunningAndQueuedRows_StageDotsProgress()
    {
        using var e = new VmEnv(parallel: 2);
        e.Vm.AddPaths(new[] { e.Zip("SM_A_001"), e.Zip("SM_A_002"), e.Zip("SM_A_003") });
        e.PumpUntil(() => e.Vm.Rows.Count(r => r.Kind == ConverterRowKind.Running && r.StageLabel == "Текстуры") == 2, "две строки на этапе текстур");

        Assert.Equal(ConverterScreen.Running, e.Vm.Screen);
        Assert.Equal(3, e.Vm.Rows.Count);
        var running = e.Row("SM_A_001");
        Assert.Equal(new[] { "on", "on", "cur", "off", "off" }, running.Dots);
        Assert.True(running.ShowDots && running.ShowBar && running.CanCancel);
        Assert.StartsWith("≈ ", running.RemainingText);
        var queued = e.Row("SM_A_003");
        Assert.Equal(ConverterRowKind.Queued, queued.Kind);
        Assert.Equal("В очереди", queued.StageLabel);
        Assert.Equal(ConverterColors.Dim, queued.CellColor);
        Assert.True(queued.ShowDash && queued.CanCancel);
        Assert.Equal("—", queued.ElapsedText);
        Assert.Equal("Идёт конвертация: готово 0 из 3, в работе 2, в очереди 1", e.Vm.ProgressTitle);
        Assert.Matches(@"^\d+ % · прошло \d+:\d\d · осталось ", e.Vm.OverallMeta);
        Assert.True(e.Vm.CancelAllCommand.CanExecute(null));
        Assert.Contains(e.Log, l => l.StartsWith("Конвертер: добавлено 3", StringComparison.Ordinal));
    }

    [Fact]
    public void CancelRow_CancelsExactlyThatRow()
    {
        using var e = new VmEnv(parallel: 2);
        e.Vm.AddPaths(new[] { e.Zip("SM_B_001"), e.Zip("SM_B_002"), e.Zip("SM_B_003") });
        e.PumpUntil(() => e.Vm.Rows.Count(r => r.Kind == ConverterRowKind.Running) == 2, "две строки в работе");

        e.Row("SM_B_002").CancelCommand.Execute(null);
        e.Converter.ReleaseAll();
        e.PumpUntil(() => e.Vm.Screen == ConverterScreen.Finished, "запуск закончен");

        Assert.Equal(ConverterRowKind.Done, e.Row("SM_B_001").Kind);
        Assert.Equal(ConverterRowKind.Cancelled, e.Row("SM_B_002").Kind);
        Assert.Equal(ConverterRowKind.Done, e.Row("SM_B_003").Kind);
        Assert.Equal("Отменено — результата нет.", e.Row("SM_B_002").OutcomeText);
        Assert.Equal("Отменено: готово 2 из 3", e.Vm.SummaryDoneText);
        Assert.Equal(ConverterColors.Dim, e.Vm.SummaryDoneColor);
    }

    [Fact]
    public void CancelQueuedRow_OthersFinish()
    {
        using var e = new VmEnv(parallel: 1);
        e.Vm.AddPaths(new[] { e.Zip("SM_Q_001"), e.Zip("SM_Q_002") });
        e.PumpUntil(() => e.Row("SM_Q_001").Kind == ConverterRowKind.Running, "первая в работе");
        e.Row("SM_Q_002").CancelCommand.Execute(null);
        e.PumpUntil(() => e.Row("SM_Q_002").Kind == ConverterRowKind.Cancelled, "вторая отменена");
        Assert.Equal(ConverterRowKind.Running, e.Row("SM_Q_001").Kind);
        e.Converter.ReleaseAll();
        e.PumpUntil(() => e.Vm.Screen == ConverterScreen.Finished, "запуск закончен");
        Assert.Equal(ConverterRowKind.Done, e.Row("SM_Q_001").Kind);
    }

    [Fact]
    public void CancelAll_AllCancelled_SummaryIsNotDone_HistoryRecorded()
    {
        using var e = new VmEnv(parallel: 2);
        e.Vm.AddPaths(new[] { e.Zip("SM_C_001"), e.Zip("SM_C_002"), e.Zip("SM_C_003") });
        e.PumpUntil(() => e.Vm.Rows.Count(r => r.Kind == ConverterRowKind.Running) == 2, "две строки в работе");

        e.Vm.CancelAllCommand.Execute(null);
        e.PumpUntil(() => e.Vm.Screen == ConverterScreen.Finished && e.Vm.History.Count == 1, "всё отменено и записано");

        Assert.All(e.Vm.Rows, r => Assert.Equal(ConverterRowKind.Cancelled, r.Kind));
        Assert.Equal("Отменено: готово 0 из 3", e.Vm.SummaryDoneText);
        Assert.Equal("", e.Vm.SummaryErrorText);
        Assert.Equal("Отменено: готово 0 из 3", e.Vm.History[0].SummaryDoneText);
        Assert.Contains(e.Log, l => l.Contains("отменить всё — 3", StringComparison.Ordinal));
    }

    [Fact]
    public void Error_RowShowsStageAndText_SummaryCountsErrors_ReportIsFailure()
    {
        using var e = new VmEnv(parallel: 2);
        e.Converter.Fail = name => name == "SM_E_002" ? new InvalidDataException("в архиве нет FBX") : null;
        e.Vm.AddPaths(new[] { e.Zip("SM_E_001"), e.Zip("SM_E_002"), e.Zip("SM_E_003") });
        e.Converter.ReleaseAll();
        e.PumpUntil(() => e.Vm.Screen == ConverterScreen.Finished, "запуск закончен");

        var bad = e.Row("SM_E_002");
        Assert.Equal(ConverterRowKind.Error, bad.Kind);
        Assert.True(bad.IsError);
        Assert.StartsWith("Ошибка на этапе «", bad.ErrorLong);
        Assert.Contains("в архиве нет FBX", bad.ErrorLong);
        Assert.Equal("Ошибка: в архиве нет FBX.", bad.OutcomeText);
        Assert.Equal(ConverterColors.Warn, bad.OutcomeColor);
        Assert.False(bad.OpenFolderCommand.CanExecute(null));
        Assert.True(bad.ReportCommand.CanExecute(null));

        Assert.Equal("✓ Готово 2 из 3", e.Vm.SummaryDoneText);
        Assert.Equal(", ", e.Vm.SummarySeparator);
        Assert.Equal("1 ошибка", e.Vm.SummaryErrorText);
        Assert.Equal(ConverterColors.Ok, e.Vm.SummaryDoneColor);

        bad.ReportCommand.Execute(null);
        var report = Assert.Single(e.Ui.Reports);
        Assert.True(report.IsFailure);
        Assert.Contains("нет FBX", report.ErrorText);
        Assert.StartsWith("Этап: ", report.ErrorStage);
        Assert.Contains("Ошибка", report.PlainText);
    }

    [Fact]
    public void Done_RowActions_OpenFolderCopyPathReport_StudioAndNewRun()
    {
        using var e = new VmEnv();
        e.Vm.AddPaths(new[] { e.Zip("SM_D_001") });
        e.Converter.ReleaseAll();
        e.PumpUntil(() => e.Vm.Screen == ConverterScreen.Finished, "запуск закончен");

        var row = e.Row("SM_D_001");
        string zip = row.Snapshot.ZipPath!;
        Assert.True(File.Exists(zip));
        Assert.StartsWith("✓ Готово · ", row.StageLabel);
        Assert.Equal(100, row.PercentValue);
        row.OpenFolderCommand.Execute(null);
        row.CopyPathCommand.Execute(null);
        row.ReportCommand.Execute(null);
        Assert.Equal(new[] { zip }, e.Ui.Selected);
        Assert.Equal(new[] { zip }, e.Ui.Copied);
        var report = Assert.Single(e.Ui.Reports);
        Assert.True(report.IsPart);
        Assert.Equal(4, report.Levels.Count);
        Assert.Equal("Исходник (FBX)", report.Levels[0].Level);
        Assert.Equal("L0 — издалека", report.Levels[1].Level);
        Assert.Equal("25 %", report.Levels[1].Share);
        Assert.Equal("99 % точек ближе 12 см, наибольшее 25 см", report.Levels[1].Accuracy);
        Assert.Equal("не упрощался; координаты округлены ≤ 1 см", report.Levels[3].Accuracy);
        Assert.Contains(report.Files, f => f.Key == "Результат");

        Assert.Equal("✓ Готово 1 из 1", e.Vm.SummaryDoneText);
        Assert.Contains("SM_D_001.glb.zip", e.Vm.StudioHint);
        e.Vm.OpenStudioCommand.Execute(null);
        Assert.Equal(new[] { AgrConverterConstants.StudioUrl }, e.Ui.Urls);
        e.Vm.OpenResultsCommand.Execute(null);
        Assert.Equal(Path.GetDirectoryName(zip), Assert.Single(e.Ui.Opened));

        string firstFolder = e.Vm.Service.CurrentRun!.Folder;
        e.Vm.NewRunCommand.Execute(null);
        Assert.Equal(ConverterScreen.Intake, e.Vm.Screen);
        e.Vm.AddPaths(new[] { e.Zip("SM_D_002") });
        e.PumpUntil(() => e.Vm.Rows.Count == 1 && e.Vm.Rows[0].Name == "SM_D_002", "новый запуск");
        Assert.NotEqual(firstFolder, e.Vm.Service.CurrentRun!.Folder);
    }

    [Fact]
    public void AddAfterFinished_StartsNewRun()
    {
        using var e = new VmEnv();
        e.Vm.AddPaths(new[] { e.Zip("SM_N_001") });
        e.Converter.ReleaseAll();
        e.PumpUntil(() => e.Vm.Screen == ConverterScreen.Finished, "первый запуск");
        var first = e.Vm.Service.CurrentRun!.Id;
        e.Vm.AddPaths(new[] { e.Zip("SM_N_002") });
        e.Pump();
        Assert.NotEqual(first, e.Vm.Service.CurrentRun!.Id);
        Assert.Equal(new[] { "SM_N_002" }, e.Vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public void History_RemoveOnlyRecord_FilesStay()
    {
        using var e = new VmEnv();
        e.Vm.AddPaths(new[] { e.Zip("SM_H_001") });
        e.Converter.ReleaseAll();
        e.PumpUntil(() => e.Vm.Screen == ConverterScreen.Finished && e.Vm.History.Count == 1, "запуск в истории");

        var item = e.Vm.History[0];
        string folder = item.Run.Folder;
        string zip = e.Row("SM_H_001").Snapshot.ZipPath!;
        Assert.Equal("✓ Готово 1 из 1", item.SummaryDoneText);
        Assert.Equal("1", item.PartsText);
        Assert.Equal(ConverterText.RunFolderShort(folder), item.FolderText);

        item.OpenFolderCommand.Execute(null);
        Assert.Equal(folder, Assert.Single(e.Ui.Opened));
        item.ReportCommand.Execute(null);
        var runReport = Assert.Single(e.Ui.Reports);
        Assert.True(runReport.IsRun);
        var part = Assert.Single(runReport.Parts);
        part.ReportCommand.Execute(null);
        Assert.True(e.Ui.Reports[^1].IsPart);

        item.RemoveCommand.Execute(null);
        e.Pump();
        Assert.Empty(e.Vm.History);
        Assert.True(e.Vm.HistoryEmpty);
        Assert.Empty(e.Vm.Service.History.Runs);
        Assert.True(File.Exists(zip), "файл результата должен остаться");
        Assert.True(Directory.Exists(folder), "папка запуска должна остаться");
    }

    [Fact]
    public void Log_OnlyThroughPost_BufferedUntilShellBinds()
    {
        using var e = new VmEnv(bindLog: false);
        e.Vm.AddPaths(new[] { e.Zip("SM_L_001") });
        e.Converter.ReleaseAll();
        e.PumpUntil(() => e.Vm.Screen == ConverterScreen.Finished, "запуск закончен");
        Assert.Empty(e.Log);
        e.BindLog();
        Assert.Contains(e.Log, l => l.StartsWith("Конвертер: добавлено 1", StringComparison.Ordinal));
        Assert.Contains(e.Log, l => l.StartsWith("Конвертер: SM_L_001 — готово", StringComparison.Ordinal));
        Assert.All(e.LogThreads, t => Assert.Equal(e.UiThread, t));
        Assert.All(e.Log, l => Assert.StartsWith(AgrConverterConstants.LogPrefix, l));
    }

    [Fact]
    public void Log_BoundBeforeRun_WorkerLinesArriveOnlyThroughPost()
    {
        using var e = new VmEnv();
        e.Vm.AddPaths(new[] { e.Zip("SM_L_002") });
        e.Converter.ReleaseAll();
        e.PumpUntil(() => e.Vm.Screen == ConverterScreen.Finished, "запуск закончен");
        Assert.Contains(e.Log, l => l.StartsWith("Конвертер: SM_L_002 — готово", StringComparison.Ordinal));
        Assert.All(e.LogThreads, t => Assert.Equal(e.UiThread, t));
    }

    [Fact]
    public void Dispose_CancelsRunningParts_HistoryClosedBeforeReturn()
    {
        using var e = new VmEnv();
        e.Converter.CancelDelayMs = 150; // запись итога приходит не сразу — Dispose должен её дождаться
        e.Vm.AddPaths(new[] { e.Zip("SM_D_1"), e.Zip("SM_D_2"), e.Zip("SM_D_3") });
        e.PumpUntil(() => e.Vm.Rows.Count(r => r.Kind == ConverterRowKind.Running) == 2, "двух идущих");
        e.Vm.Dispose(); // закрытие окна
        var run = Assert.Single(e.Vm.Service.History.Runs);
        Assert.NotNull(run.FinishedAt);
        Assert.False(run.Interrupted);
        Assert.Equal(3, run.Cancelled);
        Assert.All(e.Vm.Service.Rows, r => Assert.Equal(AgrRowState.Cancelled, r.State));
    }

    [Fact]
    public void Crash_MidRun_NextStartShowsInterrupted_Removable_DisposeWaitBounded()
    {
        using var e = new VmEnv(shutdownWait: TimeSpan.FromMilliseconds(300));
        e.Converter.IgnoreCancel = true; // отмену не слышит (завис gltfpack); SM_I_1 отпускается воротами
        e.Vm.AddPaths(new[] { e.Zip("SM_I_1"), e.Zip("SM_I_2") });
        e.PumpUntil(() => e.Vm.Rows.Count(r => r.Kind == ConverterRowKind.Running) == 2, "двух идущих");
        e.Converter.Release("SM_I_1");
        e.PumpUntil(() => e.Row("SM_I_1").Kind == ConverterRowKind.Done && e.Vm.History.Count == 1, "первой готовой в истории");
        var hung = e.Vm.History.Single();
        Assert.Equal("Идёт: готово 1 из 2", hung.SummaryDoneText);
        Assert.False(hung.RemoveCommand.CanExecute(null));
        Assert.Null(e.Vm.Service.History.Runs.Single().FinishedAt);

        // Коннектор упал посреди запуска (Dispose не было): в файле истории запись без FinishedAt.
        // Следующий старт закрывает её «Прервано» и даёт убрать.
        var ui = new FakeUi();
        using (var next = new ConverterViewModel(
                   log => new AgrConverterService(new GateConverter(), new AgrConverterStorage(e.StorageDir),
                       new AgrConverterServiceOptions { WorkRoot = Path.Combine(e.StorageDir, "..", "work2"), Log = log }),
                   ui, a => a()))
        {
            next.RefreshHistory();
            var item = Assert.Single(next.History);
            Assert.True(item.Run.Interrupted);
            Assert.Equal("Прервано: готово 1 из 2", item.SummaryDoneText);
            Assert.Equal(ConverterColors.Dim, item.SummaryDoneColor);
            Assert.True(item.RemoveCommand.CanExecute(null));
            Assert.Contains("Прервано: готово 1 из 2", ConverterReportViewModel.ForRun(item.Run, ui).Meta);
            var reread = new AgrConverterHistory(next.Service.History.Path);
            reread.Load();
            Assert.True(reread.Runs.Single().Interrupted); // отметка записана в файл
            item.RemoveCommand.Execute(null);
            Assert.Empty(next.History);
            Assert.Empty(next.Service.History.Runs);
        }

        // Закрытие окна при зависшем конвертере: ожидание ограничено, окно не держим.
        var sw = Stopwatch.StartNew();
        e.Vm.Dispose();
        sw.Stop();
        Assert.InRange(sw.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.Null(e.Vm.Service.History.Runs.Single().FinishedAt); // не дождались — при следующем старте «Прервано»
        e.Converter.ReleaseAll();
    }

    [Fact]
    public void RunningRow_ShowsItsFraction_NotZero()
    {
        using var e = new VmEnv();
        e.Vm.AddPaths(new[] { e.Zip("SM_F_1") });
        e.PumpUntil(() => e.Row("SM_F_1").StageLabel == "Текстуры", "этапа «Текстуры»");
        var row = e.Row("SM_F_1");
        double fraction = e.Vm.Service.Rows.Single().Fraction;
        Assert.True(fraction > 0, "служба: доля строки > 0");
        Assert.Equal((int)Math.Floor(fraction * 100), row.PercentValue);
        Assert.Equal($"{row.PercentValue} %", row.PercentText);
        Assert.True(row.PercentValue > 0);
    }

    [Fact]
    public void History_RemoveMiddleOfThree_OnlyThatRecordGoes()
    {
        using var e = new VmEnv();
        e.Converter.ReleaseAll();
        for (int i = 1; i <= 3; i++)
        {
            e.Vm.AddPaths(new[] { e.Zip("SM_H_" + i) });
            e.PumpUntil(() => e.Vm.Screen == ConverterScreen.Finished && e.Vm.History.Count == i, "запуска " + i);
        }
        var ids = e.Vm.History.Select(h => h.Id).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        e.Vm.History[1].RemoveCommand.Execute(null);
        e.Pump();
        Assert.Equal(new[] { ids[0], ids[2] }, e.Vm.History.Select(h => h.Id));
        Assert.Equal(new[] { ids[0], ids[2] }, e.Vm.Service.History.Runs.Select(r => r.Id));
    }

    [Theory]
    [InlineData(1, "1 ошибка")]
    [InlineData(2, "2 ошибки")]
    [InlineData(5, "5 ошибок")]
    [InlineData(11, "11 ошибок")]
    [InlineData(21, "21 ошибка")]
    [InlineData(24, "24 ошибки")]
    public void Plural_Errors(int n, string expected) => Assert.Equal(expected, ConverterText.Errors(n));

    [Fact]
    public void Texts_AsInMockup()
    {
        Assert.Equal("Идёт конвертация: готово 3 из 16, в работе 2, ошибка 1, в очереди 10", ConverterText.ProgressTitle(16, 3, 2, 1, 0, 10));
        Assert.Equal("≈ 13 мин", ConverterText.ApproxTotal(TimeSpan.FromSeconds(12 * 60 + 20)));
        Assert.Equal("17 мин 48 с", ConverterText.Human(TimeSpan.FromSeconds(17 * 60 + 48)));
        Assert.Equal("1 мин 46 с", ConverterText.Human(TimeSpan.FromSeconds(106)));
        Assert.Equal("1:46", ConverterText.Clock(TimeSpan.FromSeconds(106)));
        Assert.Equal("225,0 МБ", ConverterText.Mb(225_000_000));
        Assert.Equal("480 МБ", ConverterText.Total(480_400_000));
        Assert.Equal("по две", ConverterText.ByN(2));
        Assert.Equal("Конвертер\\2026-09-24_22-40", ConverterText.RunFolderShort(@"C:\Users\u\Documents\Structura\Конвертер\2026-09-24_22-40"));

        var mixed = ConverterText.Summary(16, 15, 1, 0, checkWhenAnyDone: true);
        Assert.Equal(("✓ Готово 15 из 16", ", ", "1 ошибка"), (mixed.DoneText, mixed.Separator, mixed.ErrorText));
        var hist = ConverterText.Summary(16, 15, 1, 0, checkWhenAnyDone: false);
        Assert.Equal("Готово 15 из 16", hist.DoneText);
        Assert.Equal("✓ Готово 1 из 1", ConverterText.Summary(1, 1, 0, 0, false).DoneText);
        var cancelled = ConverterText.Summary(16, 5, 0, 11, true);
        Assert.Equal("Отменено: готово 5 из 16", cancelled.DoneText);
        Assert.Equal(ConverterColors.Dim, cancelled.DoneColor);
    }

    /// <summary>
    /// Ревью C2a №1: служба пишет журнал, держа свой замок, а поток окна может ждать тот же замок. Перевод в поток окна
    /// через BeginInvoke отпускает рабочий поток; Invoke здесь заклинил бы оба.
    /// </summary>
    [Fact]
    public async Task Dispatch_Post_DoesNotBlockWorkerHoldingLock()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);
        var ui = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        Assert.True(ready.Wait(VmEnv.Wait));
        var post = ConverterDispatch.Post(dispatcher!);

        object gate = new();
        using var workerHolds = new ManualResetEventSlim(false);
        using var uiWaits = new ManualResetEventSlim(false);
        int delivered = 0;
        _ = dispatcher!.BeginInvoke(() =>
        {
            workerHolds.Wait();
            uiWaits.Set();
            lock (gate) { }   // поток окна ждёт замок службы
        });
        var worker = Task.Run(() =>
        {
            lock (gate)
            {
                workerHolds.Set();
                uiWaits.Wait();
                Thread.Sleep(50);
                post(() => Interlocked.Increment(ref delivered));   // журнал из-под замка службы
            }
        });
        var first = await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(first == worker, "рабочий поток заблокирован переводом в поток окна");
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref delivered) == 0 && sw.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(5);
        Assert.Equal(1, delivered);
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
    }
}

/// <summary>Живой прогон одной части через вид-модель: нужны AGR_REF_ROOT (batch\src) и AGR_GLTFPACK.</summary>
public sealed class LiveConverterFactAttribute : FactAttribute
{
    public static string? RefRoot => Environment.GetEnvironmentVariable("AGR_REF_ROOT") is { Length: > 0 } v && Directory.Exists(v) ? v : null;
    public static string? Exe => Environment.GetEnvironmentVariable("AGR_GLTFPACK") is { Length: > 0 } v && File.Exists(v) ? v : null;

    public LiveConverterFactAttribute()
    {
        if (RefRoot == null || Exe == null) Skip = "нет AGR_REF_ROOT или AGR_GLTFPACK — живой прогон пропущен";
    }
}

public class ConverterViewModelLiveTests
{
    [LiveConverterFact]
    public void Live_001_ViewModel_RowDone_ReportAsData()
    {
        const string part = "SM_ProektiruemyjProezd_001";
        string src = Path.Combine(LiveConverterFactAttribute.RefRoot!, "batch", "src", part);
        Assert.True(Directory.Exists(src), src);
        string dir = Path.Combine(Path.GetTempPath(), "c2b-live-" + Guid.NewGuid().ToString("N")[..8]);
        var posted = new ConcurrentQueue<Action>();
        var ui = new FakeUi();
        var log = new List<string>();
        var vm = new ConverterViewModel(
            l => new AgrConverterService(new AgrGltfpackPartConverter(LiveConverterFactAttribute.Exe!),
                new AgrConverterStorage(Path.Combine(dir, "storage")),
                new AgrConverterServiceOptions { WorkRoot = Path.Combine(dir, "work"), Log = l }),
            ui, a => posted.Enqueue(a)) { Log = log.Add };
        try
        {
            vm.Service.SetOutputRoot(Path.Combine(dir, "out"));
            var sw = Stopwatch.StartNew();
            vm.AddPaths(new[] { src });
            var seen = new HashSet<string>();
            while (vm.Screen != ConverterScreen.Finished)
            {
                while (posted.TryDequeue(out var a)) a();
                vm.Tick();
                if (vm.Rows.Count == 1) seen.Add(vm.Rows[0].StageLabel);
                Assert.True(sw.Elapsed < TimeSpan.FromMinutes(8), "живой прогон не закончился за 8 мин");
                Thread.Sleep(200);
            }
            while (posted.TryDequeue(out var a)) a();
            var row = Assert.Single(vm.Rows);
            Assert.True(row.Kind == ConverterRowKind.Done, row.ErrorLong);
            Assert.StartsWith("✓ Готово · ", row.StageLabel);
            Assert.True(File.Exists(row.Snapshot.ZipPath));
            row.ReportCommand.Execute(null);
            var report = Assert.Single(ui.Reports);
            Assert.True(report.IsPart, report.PlainText);
            Assert.Equal(4, report.Levels.Count);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "c2b-live-report.txt"),
                $"elapsed={sw.Elapsed}\nstages={string.Join(" | ", seen)}\n\n{report.PlainText}\n\nlog:\n{string.Join("\n", log)}");
        }
        finally
        {
            vm.Dispose();
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
