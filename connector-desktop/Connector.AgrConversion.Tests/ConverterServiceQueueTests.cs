using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Connector.AgrConversion.Service;

namespace Connector.AgrConversion.Tests;

/// <summary>
/// C2a-fix: гонки очереди и истории, защищённые события, папка запуска, отчёт строки с ошибкой. Тесты ревью C2a
/// (лимит при Add и SetParallelism, гонка «Отменить всё», правила повтора) перенесены сюда.
/// </summary>
public class ConverterServiceQueueTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    sealed class Env : IDisposable
    {
        public readonly TempDir Dir = new("c2a-q");
        public readonly FakeConverter Fake = new();
        public readonly ConcurrentQueue<string> Log = new();
        public readonly AgrConverterService Service;
        public string OutRoot => Path.Combine(Dir.Path, "out");
        public string StorageDir => Path.Combine(Dir.Path, "storage");

        public Env(int? parallel = null, TimeProvider? time = null)
        {
            Directory.CreateDirectory(Path.Combine(Dir.Path, "in"));
            Service = new AgrConverterService(Fake, new AgrConverterStorage(StorageDir),
                new AgrConverterServiceOptions { WorkRoot = Path.Combine(Dir.Path, "work"), Log = l => Log.Enqueue(l), Time = time });
            Service.SetOutputRoot(OutRoot);
            if (parallel is int p) Service.SetParallelism(p);
        }

        public string Zip(string name)
        {
            string zp = Path.Combine(Dir.Path, "in", name + ".zip");
            using var z = ZipFile.Open(zp, ZipArchiveMode.Create);
            using var w = new StreamWriter(z.CreateEntry($"{name}/{name}.fbx").Open());
            w.Write("FBX");
            return zp;
        }

        public void Dispose()
        {
            Fake.Release.Set();
            try { Service.WaitIdleAsync().Wait(Wait); } catch (AggregateException) { }
            Service.Dispose();
            Dir.Dispose();
        }
    }

    static void WaitUntil(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > Wait) throw new TimeoutException("не дождались: " + what);
            Thread.Sleep(5);
        }
    }

    internal static bool IdleWithin(AgrConverterService s, TimeSpan max) => s.WaitIdleAsync().Wait(max);

    static void Idle(AgrConverterService s) =>
        Assert.True(IdleWithin(s, Wait), "очередь не опустела: " + string.Join("; ", s.Rows));

    /// <summary>Очередь пуста, а запись истории последней строки ещё может идти — ждём законченную запись.</summary>
    static AgrHistoryRun HistoryFinished(AgrConverterService s)
    {
        WaitUntil(() => s.History.Runs.FirstOrDefault()?.FinishedAt != null, "законченной записи истории");
        return s.History.Runs[0];
    }

    [Fact]
    public void History_ParallelFinish_LastSnapshotComplete()
    {
        for (int i = 0; i < 5; i++)
        {
            using var e = new Env(8);
            // Неполный снимок (запуск ещё идёт) задерживается между сборкой и записью: без общего замка он лёг бы
            // поверх полного, собранного позже.
            e.Service.OnHistorySnapshot = snap => { if (snap.FinishedAt == null) Thread.Sleep(40); };
            int finished = 0;
            e.Service.RunFinished += (_, _) => Interlocked.Increment(ref finished);
            e.Service.Add(Enumerable.Range(1, 8).Select(k => e.Zip($"SM_P{i}_{k}")).ToArray());
            WaitUntil(() => e.Fake.Current == 8, "восьми идущих");
            e.Fake.Release.Set();
            WaitUntil(() => Volatile.Read(ref finished) == 1, "конца запуска");
            Idle(e.Service);
            Thread.Sleep(400); // запоздавшая запись старого снимка (без замка) успела бы лечь поверх
            var file = new AgrConverterHistory(Path.Combine(e.StorageDir, AgrConverterStorage.HistoryFileName));
            file.Load();
            foreach (var (h, where) in new[] { (e.Service.History, "память"), (file, "файл") })
            {
                var run = Assert.Single(h.Runs);
                Assert.True(run.Parts.Count == 8 && run.Done == 8 && run.FinishedAt != null,
                    $"итерация {i}, {where}: частей {run.Parts.Count}, готово {run.Done}, FinishedAt={run.FinishedAt}");
            }
        }
    }

    [Fact]
    public void Events_HandlersThrow_QueueRunsToEnd()
    {
        using var e = new Env(2);
        var s = e.Service;
        int finished = 0;
        static void Boom(object? o, PropertyChangedEventArgs a) => throw new InvalidOperationException("PropertyChanged");
        s.RowsChanged += (_, _) =>
        {
            foreach (var r in s.Rows)
            {
                r.PropertyChanged -= Boom;
                r.PropertyChanged += Boom;
            }
            throw new InvalidOperationException("RowsChanged");
        };
        s.RowChanged += (_, _) => throw new InvalidOperationException("RowChanged");
        s.RunFinished += (_, _) =>
        {
            Interlocked.Increment(ref finished);
            throw new InvalidOperationException("RunFinished");
        };
        s.History.Changed += (_, _) => throw new InvalidOperationException("History.Changed");
        e.Fake.Release.Set();

        var rows = s.Add(Enumerable.Range(1, 4).Select(k => e.Zip($"SM_E_{k}")).ToArray());
        Assert.Equal(4, rows.Count);
        WaitUntil(() => Volatile.Read(ref finished) == 1, "конца запуска");
        Idle(s);
        Assert.All(s.Rows, r => Assert.Equal(AgrRowState.Done, r.State));
        var run = HistoryFinished(s);
        Assert.Equal(4, run.Done);
        foreach (var name in new[] { "RowsChanged", "RowChanged", "RunFinished", "PropertyChanged" })
        {
            Assert.Contains(e.Log, l => l.Contains($"обработчик {name} бросил исключение"));
        }
        Assert.Contains(e.Log, l => l.Contains("обработчик истории бросил исключение"));
    }

    /// <summary>
    /// Порча ревью M2 («отмена только для строки в работе»): Pump уже снял строку из очереди, но ещё не пометил её
    /// «в работе». Отмена в этом окне (из обработчика RowChanged соседней строки) не должна теряться.
    /// </summary>
    [Fact]
    public void Cancel_RowDequeuedNotYetRunning_IsCancelled()
    {
        using var e = new Env(2);
        var s = e.Service;
        int fired = 0;
        bool? result = null;
        AgrConversionRow? target = null;
        s.RowChanged += (_, r) =>
        {
            if (r.State != AgrRowState.Running || Interlocked.Exchange(ref fired, 1) == 1) return;
            target = s.Rows.FirstOrDefault(x => x.Id != r.Id && x.State == AgrRowState.Queued);
            if (target != null) result = s.Cancel(target.Id);
        };
        s.Add(new[] { e.Zip("SM_W_1"), e.Zip("SM_W_2") });
        Assert.NotNull(target);
        Assert.True(result, "отмена строки в окне старта потерялась");
        WaitUntil(() => target!.IsFinished, "отмены второй строки");
        Assert.Equal(AgrRowState.Cancelled, target!.State);
        Assert.Equal(1, e.Fake.Current);
        e.Fake.Release.Set();
        Idle(s);
        Assert.Equal(AgrRowState.Done, s.Rows.Single(r => r.Id != target.Id).State);
    }

    [Fact]
    public void RunFolder_NotCreatedUntilResult_NoEmptyFolderAfterCancelAll()
    {
        using var e = new Env();
        var s = e.Service;
        s.Add(new[] { e.Zip("SM_F_1"), e.Zip("SM_F_2"), e.Zip("SM_F_3") });
        string folder = s.CurrentRun!.Folder;
        WaitUntil(() => e.Fake.Current == 2, "двух идущих");
        Assert.False(Directory.Exists(folder), "папка запуска создана до первой готовой части");
        Assert.Equal(3, s.CancelAll());
        Idle(s);
        Assert.All(s.Rows, r => Assert.Equal(AgrRowState.Cancelled, r.State));
        Assert.False(Directory.Exists(folder), "после «Отменить всё» осталась пустая папка с датой");
    }

    [Fact]
    public void OutputRootUnavailable_AddDoesNotThrow_RowError()
    {
        using var e = new Env();
        string blocker = Path.Combine(e.Dir.Path, "blocker");
        File.WriteAllText(blocker, "файл вместо папки");
        e.Service.SetOutputRoot(Path.Combine(blocker, "out"));
        e.Fake.Release.Set();
        var row = Assert.Single(e.Service.Add(new[] { e.Zip("SM_U_1") }));
        Idle(e.Service);
        WaitUntil(() => row.IsFinished, "конца строки");
        Assert.Equal(AgrRowState.Error, row.State);
        Assert.StartsWith("папка результатов недоступна", row.Error);
        Assert.True(AgrConverterService.LoadReport(row).IsFailure);
    }

    [Fact]
    public void Report_ErrorRows_FailureReport_NotThrow()
    {
        using var e = new Env(2);
        var s = e.Service;
        string notAgr = Path.Combine(e.Dir.Path, "in", "photos.zip");
        using (var z = ZipFile.Open(notAgr, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("a.txt").Open())) w.Write("x");
        e.Fake.Fail = p => p.Contains("SM_X_bad") ? new InvalidDataException("битый FBX") : null;
        var rows = s.Add(new[] { notAgr, e.Zip("SM_X_bad"), e.Zip("SM_X_ok") });
        WaitUntil(() => e.Fake.Current == 2, "двух идущих");
        e.Fake.Release.Set();
        Idle(s);
        var entry = HistoryFinished(s);
        var input = rows.Single(r => r.Name == "photos");
        var bad = rows.Single(r => r.Name == "SM_X_bad");
        var ok = rows.Single(r => r.Name == "SM_X_ok");

        var r1 = AgrConverterService.LoadReport(input);
        Assert.True(r1.IsFailure);
        Assert.Equal(AgrRowState.Error, r1.State);
        Assert.Equal("проверка ввода", r1.FailedStage);
        Assert.Contains("нет FBX", r1.Error);

        var r2 = AgrConverterService.LoadReport(bad);
        Assert.True(r2.IsFailure);
        Assert.Equal("битый FBX", r2.Error);
        Assert.Contains("gltfpack L2", r2.FailedStage);
        Assert.True(r2.Percent > 0, "не видно, сколько успела строка");
        Assert.Equal(bad.SourcePath, r2.SourcePath);

        var hp = entry.Parts.Single(p => p.Name == "SM_X_bad");
        Assert.Equal(r2.FailedStage, hp.Stage);
        var r3 = AgrConverterService.LoadReport(hp);
        Assert.True(r3.IsFailure);
        Assert.Equal("битый FBX", r3.Error);
        Assert.Equal(r2.FailedStage, r3.FailedStage);

        Assert.False(AgrConverterService.LoadReport(ok).IsFailure);
        File.Delete(ok.ZipPath!);
        File.Delete(ok.ReportPath!);
        File.Delete(ok.ManifestPath!);
        var r4 = AgrConverterService.LoadReport(ok);
        Assert.True(r4.IsFailure);
        Assert.StartsWith("отчёт не прочитан", r4.Error);
    }

    [Theory]
    [InlineData(1, "за 1 сборку")]
    [InlineData(2, "за 2 сборки")]
    [InlineData(4, "за 4 сборки")]
    [InlineData(5, "за 5 сборок")]
    [InlineData(11, "за 11 сборок")]
    [InlineData(21, "за 21 сборку")]
    public void Report_FallbackAttempts_Plural(int n, string expected)
    {
        var manifest = JsonNode.Parse(
            "{\"parts\":[{\"part\":\"SM_Pl\",\"levels\":{\"L1\":{\"triangles\":10,\"err_fwd_max_m\":0.1,\"simplify\":{\"error_m\":0.15," +
            "\"fallback\":{\"geometry_of\":\"L2\",\"after_attempts\":" + n + ",\"last_error_m\":0.2}}}}}]}")!.AsObject();
        var r = AgrPartReport.FromJson(manifest, null);
        Assert.Contains(r.Warnings, w => w.Contains("simplify.fallback — " + expected + " ошибка"));
    }

    // ---- из ревью C2a (ReviewC2aTests) ----

    [Fact]
    public void Parallelism_AddDuringWork_AndChange()
    {
        using var e = new Env(1);
        var (s, f) = (e.Service, e.Fake);
        s.Add(new[] { e.Zip("SM_Q_1"), e.Zip("SM_Q_2"), e.Zip("SM_Q_3") });
        WaitUntil(() => f.Current == 1, "одна идущая");
        s.Add(new[] { e.Zip("SM_Q_4"), e.Zip("SM_Q_5") });
        Thread.Sleep(300);
        Assert.Equal(1, f.MaxConcurrent);
        s.SetParallelism(2);
        WaitUntil(() => f.Current == 2, "две идущие");
        Thread.Sleep(300);
        Assert.Equal(2, f.MaxConcurrent);
        f.Release.Set();
        Idle(s);
        Assert.Equal(2, f.MaxConcurrent);
        Assert.All(s.Rows, r => Assert.Equal(AgrRowState.Done, r.State));
    }

    [Fact]
    public void CancelAll_RaceWithStart_Repeated()
    {
        for (int i = 0; i < 150; i++)
        {
            using var e = new Env(3);
            var s = e.Service;
            s.Add(Enumerable.Range(1, 4).Select(k => e.Zip($"SM_R{i}_{k}")).ToArray());
            if (i % 3 == 1) Thread.SpinWait(i * 50);
            s.CancelAll();
            // Release не выставлен: строка, проскочившая отмену, повиснет.
            Assert.True(IdleWithin(s, TimeSpan.FromSeconds(10)), $"итерация {i}: строка проскочила отмену: {string.Join("; ", s.Rows)}");
            Assert.All(s.Rows, r => Assert.Equal(AgrRowState.Cancelled, r.State));
            Assert.Equal(0, e.Fake.Current);
        }
    }

    [Fact]
    public void CancelFinished_AddAfterCancelAll_NewRunRules()
    {
        using var e = new Env();
        var (s, f) = (e.Service, e.Fake);
        f.Release.Set();
        var a = e.Zip("SM_A");
        s.Add(new[] { a });
        Idle(s);
        var done = s.Rows.Single();
        Assert.False(s.Cancel(done.Id));
        Assert.Equal(AgrRowState.Done, done.State);
        string folder1 = s.CurrentRun!.Folder;
        // Повтор того же файла в том же запуске (без «Новая конвертация») — пропуск.
        Assert.Empty(s.Add(new[] { a }));
        // Тот же архив под другим путём с тем же именем — строка с ошибкой имени.
        string copyDir = Path.Combine(e.Dir.Path, "copy");
        Directory.CreateDirectory(copyDir);
        File.Copy(a, Path.Combine(copyDir, "SM_A.zip"));
        var dup = Assert.Single(s.Add(new[] { Path.Combine(copyDir, "SM_A.zip") }));
        Assert.Equal(AgrRowState.Error, dup.State);
        f.Release.Reset();
        s.Add(new[] { e.Zip("SM_B"), e.Zip("SM_C") });
        s.CancelAll();
        var added = s.Add(new[] { e.Zip("SM_D") });
        f.Release.Set();
        Idle(s);
        Assert.Equal(folder1, s.CurrentRun!.Folder);
        Assert.Equal(AgrRowState.Done, added.Single().State);
    }
}

/// <summary>Живой gltfpack: отдельно от параллельных тестов, чтобы чужие gltfpack не считались своими.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveGltfpackCollection
{
    public const string Name = "живой gltfpack без параллельных тестов";
}

/// <summary>Живая отмена gltfpack (порча ревью M1 — отмена без Kill): нужны AGR_REF_ROOT и AGR_GLTFPACK.</summary>
[Collection(LiveGltfpackCollection.Name)]
public class ConverterServiceLiveTests
{
    static HashSet<int> GltfpackPids() => Process.GetProcessesByName("gltfpack").Select(p => p.Id).ToHashSet();

    [RefGltfpackFact]
    public void LiveCancel_OneRow_KillsGltfpack_CleansUp()
    {
        const string part = "SM_ProektiruemyjProezd_001";
        using var t = new TempDir("c2a-live");
        string zip = TestPaths.ZipPart(Path.Combine(TestPaths.RefRoot!, "batch", "src", part), Path.Combine(t.Path, part + ".zip"));
        string work = Path.Combine(t.Path, "work"), outRoot = Path.Combine(t.Path, "out");
        var baseline = GltfpackPids();
        using var s = new AgrConverterService(new AgrGltfpackPartConverter(GltfpackFactAttribute.Exe!),
            new AgrConverterStorage(Path.Combine(t.Path, "storage")), new AgrConverterServiceOptions { WorkRoot = work });
        s.SetOutputRoot(outRoot);
        var start = Stopwatch.StartNew();
        var row = Assert.Single(s.Add(new[] { zip }));
        var ours = new HashSet<int>();
        while (!(row.State == AgrRowState.Running && row.Phase == AgrConvertPhase.Levels && ours.Count > 0 && start.Elapsed.TotalSeconds >= 25))
        {
            Assert.True(start.Elapsed < TimeSpan.FromMinutes(4), "не дождались gltfpack в работе: " + row);
            Assert.False(row.IsFinished, "строка закончилась до отмены: " + row);
            foreach (var id in GltfpackPids().Except(baseline)) ours.Add(id);
            Thread.Sleep(100);
        }
        var cancelSw = Stopwatch.StartNew();
        Assert.True(s.Cancel(row.Id));
        Assert.True(ConverterServiceQueueTests.IdleWithin(s, TimeSpan.FromSeconds(90)), "очередь не опустела за 90 с");
        double latency = cancelSw.Elapsed.TotalSeconds;
        Assert.Equal(AgrRowState.Cancelled, row.State);
        Assert.Empty(GltfpackPids().Intersect(ours));
        Assert.True(!Directory.Exists(work) || !Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories).Any(), "временные файлы остались");
        Assert.True(!Directory.Exists(outRoot) || !Directory.EnumerateFiles(outRoot, "*", SearchOption.AllDirectories).Any(), "в папке результатов остались файлы");
        Assert.True(latency < 15, $"отмена длилась {latency:0.0} с");
    }
}
