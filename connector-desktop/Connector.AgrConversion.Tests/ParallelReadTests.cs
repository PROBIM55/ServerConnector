using System.Globalization;
using System.Text.Json.Nodes;

namespace Connector.AgrConversion.Tests;

/// <summary>
/// Один экземпляр читателя и анализатора на 8 потоков (во вкладке «Конвертер» части идут параллельно): отчёты должны
/// совпадать с последовательным прогоном. По образцу сценария ревьюера C1a: общий буфер пикселей в экземпляре давал
/// 19–34 неверных отчёта из 64 без единого исключения.
/// </summary>
public class ParallelReadTests
{
    const int Threads = 8;

    /// <summary>Отчёт части без времени: поля timings_s и ms у текстур от запуска к запуску разные.</summary>
    static string Sig(AgrPart p)
    {
        var j = JsonNode.Parse(AgrReport.ToJson(p))!.AsObject();
        j.Remove("timings_s");
        foreach (var t in j["textures"]!.AsArray()) t!.AsObject().Remove("ms");
        return j.ToJsonString();
    }

    static string Sig(AgrTextureInfo t) => string.Join("|", t.File, t.W, t.H, string.Join(",", t.Mean), string.Join(",", t.Min255),
        string.Join(",", t.Max255), t.Span255.ToString("R", CultureInfo.InvariantCulture), t.Uniform, t.HasAlpha,
        t.Alpha?.Mean, t.Alpha?.Min255, t.Alpha?.FracMid, t.Alpha?.Mode, t.ErmRed?.Mean255, t.ErmRed?.FracAbove005);

    /// <summary>
    /// <paramref name="count"/> заданий на <see cref="Threads"/> потоков, старт по общему сигналу — чтобы потоки
    /// действительно шли одновременно. Исключения не глотаются: любое роняет тест.
    /// </summary>
    static string[] RunOnThreads(int count, Func<int, string> job)
    {
        var results = new string[count];
        var errors = new List<Exception>();
        using var start = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, Threads).Select(k => new Thread(() =>
        {
            start.Wait();
            try
            {
                for (int i = k; i < count; i += Threads) results[i] = job(i);
            }
            catch (Exception ex)
            {
                lock (errors) errors.Add(ex);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        start.Set();
        threads.ForEach(t => t.Join());
        Assert.Empty(errors);
        return results;
    }

    [Fact]
    public void OnePartReader_EightThreads_64Parts_MatchSequential()
    {
        var reader = new AgrPartReader();
        string expected = Sig(reader.Read(TestPaths.FixtureDir));
        Assert.Equal(expected, Sig(reader.Read(TestPaths.FixtureDir)));

        var got = RunOnThreads(64, _ => Sig(reader.Read(TestPaths.FixtureDir)));

        int wrong = got.Count(s => s != expected);
        Assert.True(wrong == 0, $"отчётов не как в последовательном прогоне: {wrong} из 64");
    }

    [Fact]
    public void OneTextureAnalyzer_EightThreads_MatchSequential()
    {
        var files = Directory.GetFiles(TestPaths.FixtureDir, "T_*.png").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.True(files.Length >= 4, "в фикстуре мало текстур");
        var analyzer = new AgrTextureAnalyzer();
        var expected = files.Select(f => Sig(analyzer.Analyze(f, Path.GetFileName(f)))).ToArray();

        int n = files.Length * 32;
        var got = RunOnThreads(n, i => Sig(analyzer.Analyze(files[i % files.Length], Path.GetFileName(files[i % files.Length]))));

        int wrong = Enumerable.Range(0, n).Count(i => got[i] != expected[i % files.Length]);
        Assert.True(wrong == 0, $"статистика картинок не как в последовательном прогоне: {wrong} из {n}");
    }
}
