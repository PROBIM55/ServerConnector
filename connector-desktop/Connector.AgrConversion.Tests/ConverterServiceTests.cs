using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Connector.AgrConversion.Service;

namespace Connector.AgrConversion.Tests;

/// <summary>Поддельный конвертер: держит строки, пока тест не отпустит; мусорит во временной папке, как настоящий при сбое.</summary>
sealed class FakeConverter : IAgrPartConverter
{
    int _current;
    public int MaxConcurrent;
    public int Current => Volatile.Read(ref _current);
    public readonly ManualResetEventSlim Release = new(false);
    public readonly ConcurrentQueue<string> Started = new();
    public readonly ConcurrentDictionary<string, string> WorkRoots = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, Exception?>? Fail;

    public AgrConvertResult Convert(string input, string outputDirectory, string workRoot, IProgress<AgrConvertProgress> progress,
                                    CancellationToken cancellationToken)
    {
        int now = Interlocked.Increment(ref _current);
        int max;
        while (now > (max = Volatile.Read(ref MaxConcurrent)) && Interlocked.CompareExchange(ref MaxConcurrent, now, max) != max) { }
        Started.Enqueue(input);
        WorkRoots[input] = workRoot;
        string stem = "SM_Inner_" + Path.GetFileNameWithoutExtension(input); // имя части внутри ≠ имени архива
        try
        {
            Directory.CreateDirectory(workRoot);
            File.WriteAllText(Path.Combine(workRoot, "unzipped.fbx"), "мусор распаковки");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, stem + ".glb.zip.part"), "недописанный zip");
            progress.Report(new AgrConvertProgress("распаковка", 1, 10) { Phase = AgrConvertPhase.Read });
            progress.Report(new AgrConvertProgress("gltfpack L2", 5, 10) { Phase = AgrConvertPhase.Levels, PhaseFraction = 0.5 });
            WaitHandle.WaitAny(new[] { Release.WaitHandle, cancellationToken.WaitHandle });
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail?.Invoke(input) is { } ex) throw ex;
            progress.Report(new AgrConvertProgress("tileset и manifest", 10, 10) { Phase = AgrConvertPhase.Pack });
            string zipPath = Path.Combine(outputDirectory, stem + ".glb.zip");
            var manifest = ServiceSamples.Manifest(stem);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open())) w.Write(manifest.ToJsonString());
                using (var w = new StreamWriter(zip.CreateEntry("tileset.json").Open())) w.Write("{}");
            }
            File.Delete(Path.Combine(outputDirectory, stem + ".glb.zip.part"));
            return new AgrConvertResult { PartName = stem, ZipPath = zipPath, Manifest = manifest, PartReportJson = ServiceSamples.Report(stem).ToJsonString() };
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }
}

/// <summary>Образцы манифеста и отчёта части по полям C1c-1 (0d2907c).</summary>
static class ServiceSamples
{
    public static JsonObject Manifest(string part) => new()
    {
        ["package"] = part,
        ["levels"] = new JsonObject { ["L0"] = "упрощение до 0.1", ["L1"] = "упрощение до 0.5", ["L2"] = "полная геометрия, текстуры ≤ 2048" },
        ["sources"] = new JsonArray(new JsonObject { ["name"] = part + ".zip", ["kind"] = "Zip", ["bytes"] = 225_000_000 }),
        ["parts"] = new JsonArray(new JsonObject
        {
            ["part"] = part,
            ["triangles_ref"] = 76425,
            ["extent_m"] = 741.5,
            ["positions"] = "16 бит (-vp 16)",
            ["levels"] = new JsonObject
            {
                ["L0"] = Level(21983, 0.2156, 0.2045, 0.0331, new JsonObject
                {
                    ["ratio"] = 0.5, ["error_m"] = 0.5, ["attempts"] = 4, ["skipped"] = null,
                    ["fallback"] = new JsonObject { ["geometry_of"] = "L1", ["after_attempts"] = 4, ["last_error_m"] = 0.6213 },
                }),
                ["L1"] = Level(44933, 0.1191, 0.1551, 0.0107, new JsonObject { ["ratio"] = 0.5, ["error_m"] = 0.15, ["attempts"] = 4, ["fallback"] = null }),
                ["L2"] = Level(76425, 0.0092, 0.0094, 0.0074, null),
            },
            ["dropped"] = new JsonObject
            {
                ["collision"] = new JsonObject { ["UCX_"] = new JsonObject { ["objects"] = 3, ["triangles"] = 36 } },
                ["light_fbx"] = new JsonArray("SM_X_Light.fbx"),
                ["unused_textures"] = new JsonArray("T_Unused_Diffuse.png"),
                ["emissive"] = new JsonArray(),
            },
            ["warnings"] = new JsonArray("L1: ошибка 0.1551 м выше предела 0.15 м после 4 сборок"),
        }),
    };

    static JsonObject Level(long tris, double fwd, double rev, double p99, JsonObject? simplify) => new()
    {
        ["triangles"] = tris, ["bytes"] = tris * 100, ["images"] = 10, ["image_max_px"] = 2048,
        ["err_fwd_max_m"] = fwd, ["err_fwd_p99_m"] = p99, ["err_rev_max_m"] = rev, ["err_rev_p99_m"] = p99 / 2,
        ["texel_m"] = 0.003846, ["simplify"] = simplify,
    };

    public static JsonObject Report(string part) => new()
    {
        ["report_version"] = 1,
        ["part"] = part,
        ["textures_summary"] = new JsonObject { ["files"] = 36, ["analyzed"] = 36, ["real"] = 10, ["uniform"] = 26, ["unused"] = 0 },
        ["stubs"] = new JsonArray(Enumerable.Range(1, 26).Select(i => (JsonNode)new JsonObject { ["file"] = $"T_Stub_{i:00}.png" }).ToArray()),
        ["warnings"] = new JsonArray("нет карты Normal для M_Road тайл 1002"),
    };
}

sealed class FixedTime : TimeProvider
{
    public DateTimeOffset Now = new(2026, 9, 25, 14, 30, 5, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>C2a: служба конвертера — очередь, отмена, папки, история, отчёт.</summary>
public class ConverterServiceTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    sealed class Harness : IDisposable
    {
        public readonly TempDir Dir = new("c2a");
        public readonly FakeConverter Fake = new();
        public readonly ConcurrentQueue<string> Log = new();
        public AgrConverterService Service = null!;
        public string Inputs => Path.Combine(Dir.Path, "in");
        public string OutRoot => Path.Combine(Dir.Path, "out");
        public string WorkRoot => Path.Combine(Dir.Path, "work");
        public string StorageDir => Path.Combine(Dir.Path, "storage");

        public Harness(TimeProvider? time = null, bool create = true)
        {
            Directory.CreateDirectory(Inputs);
            Directory.CreateDirectory(StorageDir);
            if (create) Create(time);
        }

        public AgrConverterService Create(TimeProvider? time = null, IAgrPartConverter? converter = null)
        {
            Service = new AgrConverterService(converter ?? Fake, new AgrConverterStorage(StorageDir),
                new AgrConverterServiceOptions { WorkRoot = WorkRoot, Time = time, Log = l => Log.Enqueue(l) });
            Service.SetOutputRoot(OutRoot);
            return Service;
        }

        /// <summary>zip части: папка SM_* с главным FBX внутри, как в пакете АГР.</summary>
        public string PartZip(string name)
        {
            string zipPath = Path.Combine(Inputs, name + ".zip");
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            using (var w = new StreamWriter(zip.CreateEntry($"{name}/{name}.fbx").Open())) w.Write("FBX");
            using (var w = new StreamWriter(zip.CreateEntry($"{name}/T_{name}_1001_Diffuse.png").Open())) w.Write("PNG");
            return zipPath;
        }

        public void Dispose()
        {
            Fake.Release.Set();
            try { Service?.WaitIdleAsync().Wait(Wait); } catch (AggregateException) { }
            Service?.Dispose();
            Dir.Dispose();
        }
    }

    static void WaitUntil(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > Wait) throw new TimeoutException("не дождались: " + what);
            Thread.Sleep(10);
        }
    }

    static void Idle(AgrConverterService s) => Assert.True(s.WaitIdleAsync().Wait(Wait), "очередь не опустела");

    static IEnumerable<string> RowDirs(Harness h) =>
        Directory.Exists(h.WorkRoot) ? Directory.EnumerateDirectories(h.WorkRoot, AgrConverterPaths.RowWorkPrefix + "*") : Enumerable.Empty<string>();

    [Fact]
    public void Queue_TwoInParallel_OthersWait_ThenAllDone()
    {
        using var h = new Harness();
        var zips = Enumerable.Range(1, 5).Select(i => h.PartZip($"SM_Part_{i:000}")).ToList();
        var rows = h.Service.Add(zips);
        Assert.Equal(5, rows.Count);
        Assert.Equal(2, h.Service.Parallelism);

        WaitUntil(() => h.Fake.Current == 2, "двух идущих");
        Thread.Sleep(300); // третья не должна стартовать, пока две держатся
        Assert.Equal(2, h.Fake.Current);
        Assert.Equal(2, h.Service.Rows.Count(r => r.State == AgrRowState.Running));
        Assert.Equal(3, h.Service.Rows.Count(r => r.State == AgrRowState.Queued));
        var running = h.Service.Rows.First(r => r.State == AgrRowState.Running);
        WaitUntil(() => running.Phase == AgrConvertPhase.Levels, "этапа «уровни»");
        Assert.InRange(running.Percent, 15, 98);
        Assert.NotNull(running.Remaining);
        var overall = h.Service.GetOverall();
        Assert.Equal((5, 2, 3), (overall.Total, overall.Running, overall.Queued));

        h.Fake.Release.Set();
        Idle(h.Service);
        Assert.Equal(2, h.Fake.MaxConcurrent);
        Assert.All(h.Service.Rows, r => Assert.Equal(AgrRowState.Done, r.State));
        var run = h.Service.CurrentRun!;
        // Имя выхода — из имени архива, не из имени части внутри (Р10.2).
        foreach (var r in h.Service.Rows)
        {
            Assert.Equal(Path.Combine(run.Folder, r.Name + ".glb.zip"), r.ZipPath);
            Assert.True(File.Exists(r.ZipPath));
            Assert.True(File.Exists(r.ReportPath));
            Assert.True(File.Exists(r.ManifestPath));
            Assert.Equal(100, r.Percent);
        }
        Assert.Empty(Directory.EnumerateFiles(run.Folder, "*.part", SearchOption.AllDirectories));
        Assert.Empty(RowDirs(h));
        Assert.Equal("Готово 5 из 5", run.Summary);
        Assert.Contains(h.Log, l => l.StartsWith("Конвертер: SM_Part_001 — готово за"));
        Assert.Contains(h.Log, l => l.StartsWith("Конвертер: запуск закончен — Готово 5 из 5"));
    }

    [Fact]
    public void Parallelism_IsSetting_Stored()
    {
        using var h = new Harness();
        h.Service.SetParallelism(3);
        h.Service.Add(Enumerable.Range(1, 4).Select(i => h.PartZip($"SM_P_{i}")));
        WaitUntil(() => h.Fake.Current == 3, "трёх идущих");
        Thread.Sleep(200);
        Assert.Equal(3, h.Fake.MaxConcurrent);
        h.Fake.Release.Set();
        Idle(h.Service);
        Assert.Equal(3, h.Fake.MaxConcurrent);
        var settings = new AgrConverterStorage(h.StorageDir).LoadSettings();
        Assert.Equal(3, settings.Parallelism);
        Assert.Equal(h.OutRoot, settings.OutputRoot);
        Assert.Equal(AgrConverterConstants.DefaultParallelism, new AgrConverterSettings().EffectiveParallelism);
    }

    [Fact]
    public void CancelRow_Running_CleansTempAndPartialZip_OthersContinue()
    {
        using var h = new Harness();
        var a = h.PartZip("SM_A_001");
        var b = h.PartZip("SM_B_002");
        h.Service.Add(new[] { a, b });
        // Поддельный конвертер сообщает этап «уровни» после того, как намусорил во временной папке.
        WaitUntil(() => h.Fake.Current == 2 && h.Service.Rows.All(r => r.Phase == AgrConvertPhase.Levels), "двух идущих");
        var rowA = h.Service.Rows.Single(r => r.SourcePath == a);
        string workA = h.Fake.WorkRoots[a];
        Assert.True(File.Exists(Path.Combine(workA, "unzipped.fbx")));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(workA)!, "*.part", SearchOption.AllDirectories));

        Assert.True(h.Service.Cancel(rowA.Id));
        WaitUntil(() => rowA.IsFinished, "остановки строки");
        Assert.Equal(AgrRowState.Cancelled, rowA.State);
        Assert.False(Directory.Exists(Path.GetDirectoryName(workA)), "временная папка строки осталась");
        Assert.Null(rowA.ZipPath);
        Assert.False(File.Exists(Path.Combine(h.Service.CurrentRun!.Folder, rowA.OutputFileName)));

        h.Fake.Release.Set();
        Idle(h.Service);
        var rowB = h.Service.Rows.Single(r => r.SourcePath == b);
        Assert.Equal(AgrRowState.Done, rowB.State);
        Assert.Empty(RowDirs(h));
        Assert.Empty(Directory.EnumerateFiles(h.Service.CurrentRun!.Folder, "*.part", SearchOption.AllDirectories));
        Assert.Equal("Готово 1 из 2, отменено 1", h.Service.CurrentRun!.Summary);
        Assert.Contains(h.Log, l => l == "Конвертер: SM_A_001 — отменено, временные файлы удалены");
    }

    [Fact]
    public void CancelAll_StopsRunningAndQueued_CleansAll()
    {
        using var h = new Harness();
        h.Service.Add(Enumerable.Range(1, 5).Select(i => h.PartZip($"SM_C_{i}")));
        WaitUntil(() => h.Fake.Current == 2, "двух идущих");
        var works = h.Fake.WorkRoots.Values.ToList();

        Assert.Equal(5, h.Service.CancelAll());
        Idle(h.Service);
        Assert.All(h.Service.Rows, r => Assert.Equal(AgrRowState.Cancelled, r.State));
        Assert.Equal(2, h.Fake.Started.Count); // ожидавшие не стартовали
        Assert.All(works, w => Assert.False(Directory.Exists(Path.GetDirectoryName(w))));
        Assert.Empty(RowDirs(h));
        Assert.False(Directory.Exists(h.Service.CurrentRun!.Folder), "отменённый целиком запуск оставил папку с датой");
        var hist = h.Service.History.Runs.Single();
        Assert.Equal((5, 0, 5), (hist.Total, hist.Done, hist.Cancelled));
        Assert.NotNull(hist.FinishedAt);
    }

    [Fact]
    public void NotAgrInput_RowWithError_NoCrash_NoDuplicates()
    {
        using var h = new Harness();
        string noFbx = Path.Combine(h.Inputs, "photos.zip");
        using (var zip = ZipFile.Open(noFbx, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("readme.txt").Open())) w.Write("x");
        string notZip = Path.Combine(h.Inputs, "broken.zip");
        File.WriteAllText(notZip, "это не zip");
        string txt = Path.Combine(h.Inputs, "notes.txt");
        File.WriteAllText(txt, "x");
        string emptyDir = Path.Combine(h.Inputs, "empty");
        Directory.CreateDirectory(emptyDir);
        string good = h.PartZip("SM_Good_001");

        var rows = h.Service.Add(new[] { noFbx, notZip, txt, emptyDir, good, good });
        Assert.Equal(5, rows.Count); // повтор good в одном вызове не дублируется
        Assert.Empty(h.Service.Add(new[] { good })); // и в следующем тоже
        Assert.Contains(h.Log, l => l == "Конвертер: SM_Good_001 — уже в очереди, повтор пропущен");

        string Err(string path) => h.Service.Rows.Single(r => r.SourcePath == Path.GetFullPath(path)).Error!;
        Assert.Contains("в архиве нет FBX", Err(noFbx));
        Assert.Contains("не архив АГР", Err(notZip));
        Assert.Contains("не архив АГР", Err(txt));
        Assert.Contains("нет zip частей АГР", Err(emptyDir));
        Assert.Equal(4, h.Service.Rows.Count(r => r.State == AgrRowState.Error));

        // Папка с zip частей: берутся только zip, свои .glb.zip пропускаются.
        File.WriteAllText(Path.Combine(h.Inputs, "old.glb.zip"), "x");
        Assert.False(h.Service.NewRun()); // текущий ещё идёт — новый запуск не начинается
        Assert.Empty(h.Service.Add(new[] { h.Inputs })); // zip из папки уже в очереди, notes.txt и пустая подпапка — не части
        Assert.Equal(5, h.Service.Rows.Count);
        Assert.DoesNotContain(h.Service.Rows, r => r.Name.StartsWith("old", StringComparison.Ordinal));

        h.Fake.Release.Set();
        Idle(h.Service);
        Assert.Equal(AgrRowState.Done, h.Service.Rows.Single(r => r.SourcePath == good).State);
        Assert.Single(h.Fake.Started); // конвертер вызван только для годной части
    }

    [Fact]
    public void ConverterFailure_RowError_NoOutput()
    {
        using var h = new Harness();
        h.Fake.Fail = _ => new AgrReadException("SM_F.zip", "Архив SM_F.zip: в части нет главного файла SM_*.fbx.");
        h.Service.Add(new[] { h.PartZip("SM_F") });
        h.Fake.Release.Set();
        Idle(h.Service);
        var row = h.Service.Rows.Single();
        Assert.Equal(AgrRowState.Error, row.State);
        Assert.Equal("Архив SM_F.zip: в части нет главного файла SM_*.fbx.", row.Error);
        Assert.Empty(RowDirs(h));
        Assert.False(Directory.Exists(h.Service.CurrentRun!.Folder), "запуск без готовых частей оставил папку с датой");
        Assert.Equal("Готово 0 из 1, 1 ошибка", h.Service.CurrentRun!.Summary);
    }

    // C2c-fix Д1: Assimp не загрузился — строка с ошибкой по-русски, служба не падает и берёт следующую часть.
    [Fact]
    public void AssimpLoadFailure_RowError_ServiceContinues()
    {
        using var h = new Harness();
        var loadFailure = Assert.IsType<AgrReadException>(Record.Exception(() => AssimpFbxReader.LoadApi(
            () => throw new DllNotFoundException("Unable to load DLL 'Assimp64.dll' or one of its dependencies"))));
        h.Fake.Fail = input => Path.GetFileNameWithoutExtension(input) == "SM_NoAssimp" ? loadFailure : null;
        string bad = h.PartZip("SM_NoAssimp");
        string good = h.PartZip("SM_AfterNoAssimp");
        h.Service.Add(new[] { bad, good });
        h.Fake.Release.Set();
        Idle(h.Service);
        var badRow = h.Service.Rows.Single(r => r.SourcePath == bad);
        Assert.Equal(AgrRowState.Error, badRow.State);
        Assert.StartsWith("Не загрузилась библиотека чтения FBX (Assimp): ", badRow.Error);
        Assert.Contains("Переустановите Structura Connector", badRow.Error);
        Assert.Equal(AgrRowState.Done, h.Service.Rows.Single(r => r.SourcePath == good).State);
        Assert.Equal("Готово 1 из 2, 1 ошибка", h.Service.CurrentRun!.Summary);
    }

    [Fact]
    public void RunFolder_DateNamed_UniquePerRun_DefaultRoot()
    {
        var time = new FixedTime();
        using var h = new Harness(time);
        h.Fake.Release.Set();
        h.Service.Add(new[] { h.PartZip("SM_D_1") });
        Idle(h.Service);
        Assert.Equal(Path.Combine(h.OutRoot, "2026-09-25_14-30"), h.Service.CurrentRun!.Folder);
        Assert.True(h.Service.NewRun());
        h.Service.Add(new[] { h.PartZip("SM_D_2") });
        Idle(h.Service);
        Assert.Equal(Path.Combine(h.OutRoot, "2026-09-25_14-30_2"), h.Service.CurrentRun!.Folder);
        Assert.True(File.Exists(Path.Combine(h.OutRoot, "2026-09-25_14-30_2", "SM_D_2.glb.zip")));
        Assert.True(File.Exists(Path.Combine(h.OutRoot, "2026-09-25_14-30_2", "Отчёты", "SM_D_2.report.json")));

        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.Equal(Path.Combine(docs, "Structura", "Конвертер"), AgrConverterPaths.DefaultOutputRoot);
        Assert.Equal(Path.Combine(docs, "Structura", "Конвертер"), new AgrConverterSettings().EffectiveOutputRoot);
        Assert.EndsWith(@"\ConnectorAgentDesktop", AgrConverterPaths.DefaultStorageDirectory);
        Assert.Equal("SM_ProektiruemyjProezd_001", AgrConverterPaths.PartName(@"C:\x\SM_ProektiruemyjProezd_001.zip"));
        Assert.Equal("https://app.structura-most.ru/studio", AgrConverterConstants.StudioUrl);
    }

    [Fact]
    public void History_Recorded_Reloaded_RemoveKeepsFiles()
    {
        using var h = new Harness();
        h.Fake.Release.Set();
        h.Service.Add(new[] { h.PartZip("SM_H_1"), h.PartZip("SM_H_2") });
        Idle(h.Service);
        var run = h.Service.CurrentRun!;
        string path = Path.Combine(h.StorageDir, AgrConverterStorage.HistoryFileName);
        Assert.True(File.Exists(path));

        var reread = new AgrConverterHistory(path);
        reread.Load();
        var entry = reread.Runs.Single();
        Assert.Equal(run.Id, entry.Id);
        Assert.Equal(run.Folder, entry.Folder);
        Assert.Equal((2, 2, "Готово 2 из 2"), (entry.Total, entry.Done, entry.Summary));
        Assert.All(entry.Parts, p =>
        {
            Assert.Equal(AgrRowState.Done, p.State);
            Assert.True(File.Exists(p.ReportPath));
            Assert.True(File.Exists(p.ZipPath));
        });
        var report = AgrConverterService.LoadReport(entry.Parts[0]);
        Assert.Equal(3, report.Levels.Count);

        Assert.True(reread.Remove(entry.Id));
        var again = new AgrConverterHistory(path);
        again.Load();
        Assert.Empty(again.Runs);
        // Файлы остались: папка запуска, zip, отчёты.
        Assert.True(Directory.Exists(run.Folder));
        Assert.All(entry.Parts, p =>
        {
            Assert.True(File.Exists(p.ZipPath), "Убрать из истории удалило zip");
            Assert.True(File.Exists(p.ReportPath), "Убрать из истории удалило отчёт");
            Assert.True(File.Exists(p.ManifestPath));
        });
    }

    [Theory]
    [InlineData("{ это не json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"format\": \"чужая программа\", \"runs\": [1]}")]
    [InlineData("{\"format\": \"structura-connector-converter-history\", \"version\": 1, \"runs\": [{\"id\": 5}]}")]
    public void History_BrokenOrForeign_EmptyAndLogged_NotCrash(string content)
    {
        using var h = new Harness(create: false);
        string path = Path.Combine(h.StorageDir, AgrConverterStorage.HistoryFileName);
        File.WriteAllText(path, content);
        var service = h.Create();
        Assert.Empty(service.History.Runs);
        Assert.Contains(h.Log, l => l.StartsWith("Конвертер: история не прочитана"));
        Assert.Single(Directory.EnumerateFiles(h.StorageDir, AgrConverterStorage.HistoryFileName + ".unreadable-*"));
        // Новая запись после битой — нормальная.
        h.Fake.Release.Set();
        service.Add(new[] { h.PartZip("SM_After") });
        Idle(service);
        var reread = new AgrConverterHistory(path);
        reread.Load();
        Assert.Single(reread.Runs);
    }

    [Fact]
    public void BrokenSettings_Defaults_NotCrash()
    {
        using var h = new Harness(create: false);
        File.WriteAllText(Path.Combine(h.StorageDir, AgrConverterStorage.SettingsFileName), "{ битые настройки");
        var service = h.Create();
        Assert.Equal(2, service.Parallelism);
        Assert.Contains(h.Log, l => l.StartsWith("Конвертер: настройки не прочитаны"));
    }

    [Fact]
    public void Report_FromReadyManifest()
    {
        var r = AgrPartReport.FromJson(ServiceSamples.Manifest("SM_R_001"), ServiceSamples.Report("SM_R_001"));
        Assert.Equal("SM_R_001", r.Part);
        Assert.Equal(76425, r.TrianglesSource);
        Assert.Equal(new[] { "L0", "L1", "L2" }, r.Levels.Select(l => l.Name));
        Assert.Equal(new long[] { 21983, 44933, 76425 }, r.Levels.Select(l => l.Triangles));
        Assert.Equal(21.56, r.Levels[0].ErrorMaxCm);
        Assert.Equal(15.51, r.Levels[1].ErrorMaxCm);
        Assert.Equal(0.94, r.AccuracyCm!.Value);
        Assert.Equal(50.0, r.Levels[0].ErrorLimitCm!.Value);
        Assert.Null(r.Levels[2].ErrorLimitCm);
        Assert.Equal((36, 10, 26), (r.TextureFiles!.Value, r.TexturesReal!.Value, r.TextureStubs!.Value));
        Assert.Equal(26, r.StubFiles.Count);
        Assert.True(r.HasFallback);
        Assert.Equal("L1", r.Levels[0].FallbackGeometryOf);
        Assert.True(r.LimitExceeded);
        Assert.False(r.Levels[0].LimitExceeded);
        Assert.True(r.Levels[1].LimitExceeded);
        Assert.StartsWith("L0: simplify.fallback", r.Warnings[0]);
        Assert.Contains(r.Warnings, w => w.StartsWith("L1: ошибка 15,51 см выше предела 15 см"));
        Assert.Contains("L1: ошибка 0.1551 м выше предела 0.15 м после 4 сборок", r.Warnings);
        Assert.Contains("нет карты Normal для M_Road тайл 1002", r.Warnings);
        Assert.Contains(r.Dropped, d => d.StartsWith("картинки без граней: 1"));
        Assert.Contains(r.Dropped, d => d.StartsWith("коллизии UCX_: 3 объектов, 36 треугольников"));
        Assert.Equal(225_000_000L, r.SourceBytes!.Value);
    }

    [Fact]
    public void Report_ManifestFromZip_WhenNoCopy()
    {
        using var t = new TempDir("c2a-rep");
        string zipPath = Path.Combine(t.Path, "SM_Z.glb.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open())) w.Write(ServiceSamples.Manifest("SM_Z").ToJsonString());
        var r = AgrPartReport.Load(Path.Combine(t.Path, "нет.manifest.json"), null, zipPath);
        Assert.Equal(3, r.Levels.Count);
        Assert.Null(r.TextureStubs); // без отчёта чтения части числа заглушек нет
        Assert.Throws<FileNotFoundException>(() => AgrPartReport.Load(null, null, Path.Combine(t.Path, "нет.glb.zip")));
    }

    [Fact]
    public void Estimate_BySizeThenByProgress()
    {
        // До старта — по размеру: 100 МБ × 0,45 с/МБ = 45 с.
        Assert.Equal(TimeSpan.FromSeconds(45), AgrConversionEstimate.Remaining(TimeSpan.Zero, 0, 100_000_000, 0));
        // Половина за 30 с: по доле 30 с, по размеру 15 с → 22,5 ≈ 22 или 23.
        Assert.InRange(AgrConversionEstimate.Remaining(TimeSpan.FromSeconds(30), 0.5, 100_000_000, 0.45).TotalSeconds, 22, 23);
        Assert.Equal(TimeSpan.Zero, AgrConversionEstimate.Remaining(TimeSpan.FromSeconds(90), 1, 100_000_000, 0.45));
        // Этапы: общая доля не убывает от этапа к этапу.
        double prev = -1;
        foreach (var p in AgrConversionStages.All)
        {
            Assert.True(AgrConversionStages.Overall(p, 0) >= prev - 1e-9);
            prev = AgrConversionStages.Overall(p, 1);
        }
        Assert.Equal(1, AgrConversionStages.Overall(AgrConvertPhase.Pack, 1), 9);
        Assert.Equal("Готово 15 из 16, 1 ошибка", AgrConverterText.Summary(16, 15, 1, 0));
        Assert.Equal("Готово 11 из 16, 5 ошибок", AgrConverterText.Summary(16, 11, 5, 0));
    }
}

public sealed class RefGltfpackFactAttribute : FactAttribute
{
    public RefGltfpackFactAttribute()
    {
        if (TestPaths.RefRoot == null || GltfpackFactAttribute.Exe == null)
        {
            Skip = $"интеграция C2a: задайте {TestPaths.RefRootVariable} и {GltfpackFactAttribute.Variable}";
        }
    }
}

/// <summary>C2a: служба на настоящей части и настоящем манифесте C1c-1.</summary>
public class ConverterServiceIntegrationTests
{
    [RefGltfpackFact]
    public void RealPart001_ThroughService_DoneWithReport()
    {
        const string part = "SM_ProektiruemyjProezd_001";
        using var t = new TempDir("c2a-int");
        string zip = TestPaths.ZipPart(Path.Combine(TestPaths.RefRoot!, "batch", "src", part), Path.Combine(t.Path, part + ".zip"));
        var log = new ConcurrentQueue<string>();
        var phases = new ConcurrentDictionary<AgrConvertPhase, bool>();
        var storage = new AgrConverterStorage(Path.Combine(t.Path, "storage"));
        using var s = new AgrConverterService(new AgrGltfpackPartConverter(GltfpackFactAttribute.Exe!), storage,
            new AgrConverterServiceOptions { WorkRoot = Path.Combine(t.Path, "work"), Log = l => log.Enqueue(l) });
        s.SetOutputRoot(Path.Combine(t.Path, "out"));
        s.RowChanged += (_, r) => { if (r.Phase is { } p) phases[p] = true; };
        s.Add(new[] { zip });
        Assert.True(s.WaitIdleAsync().Wait(TimeSpan.FromMinutes(10)));
        var row = s.Rows.Single();
        Assert.True(row.State == AgrRowState.Done, row.Error);
        Assert.Equal(part + ".glb.zip", Path.GetFileName(row.ZipPath));
        Assert.Equal(AgrConversionStages.All.OrderBy(p => p), phases.Keys.OrderBy(p => p));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(t.Path, "work")));
        var report = AgrConverterService.LoadReport(row);
        Assert.Equal(new[] { "L0", "L1", "L2" }, report.Levels.Select(l => l.Name));
        Assert.True(report.TrianglesSource > 0 && report.Levels[2].Triangles == report.TrianglesSource);
        Assert.True(report.AccuracyCm <= 1.0, $"L2 {report.AccuracyCm} см");
        Assert.True(report.TextureFiles > 0 && report.TexturesReal > 0);
        File.WriteAllLines(Path.Combine(TestPaths.RefRoot!, "c2a", "logs", "integration_001.log"),
            log.Append($"report: tris {report.TrianglesSource}, L0/L1/L2 {string.Join("/", report.Levels.Select(l => l.Triangles))}, " +
                       $"err cm {string.Join("/", report.Levels.Select(l => l.ErrorMaxCm))}, textures {report.TextureFiles}/{report.TexturesReal}/{report.TextureStubs}, " +
                       $"warnings {report.Warnings.Count}, seconds {row.Elapsed.TotalSeconds:0.0}"));
    }

    [RefFact]
    public void RealC1c1Zip_ReportReadsManifest()
    {
        string zip = Path.Combine(TestPaths.RefRoot!, "c1c1", "out", "SM_ProektiruemyjProezd_Ground.glb.zip");
        Assert.True(File.Exists(zip), "нет эталонного пакета C1c-1: " + zip);
        var r = AgrPartReport.Load(null, null, zip);
        Assert.Equal("SM_ProektiruemyjProezd_Ground", r.Part);
        Assert.Equal(new[] { "L0", "L1", "L2" }, r.Levels.Select(l => l.Name));
        Assert.True(r.TrianglesSource > 0);
        Assert.Contains("float32", r.Positions);
    }
}
