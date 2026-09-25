using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Connector.AgrConversion;
using Xunit;

namespace Connector.AgrConversion.Tests;

/// <summary>
/// C1c-1-fix: требования C1 поведением, а не текстом кода — (к) ошибка в обе стороны, (о) обратная по точкам площади,
/// Р8 geometricError и тексель (с), box тайла объединяет детей, (н) fallback и порог малой части, детерминизм пакета.
/// </summary>
public class PackRequirementTests
{
    static readonly AgrConvertOptions Opt = new() { GltfpackPath = "gltfpack.exe", MinAreaSamples = 20_000 };

    /// <summary>Сетка в плоскости z = 0 из прямоугольников (x0, y0, x1, y1), по два треугольника на каждый; UV = x/10, y/10.</summary>
    static AgrGeomPrimitive Quads(params (double X0, double Y0, double X1, double Y1)[] rects)
    {
        var pos = new List<double>();
        var uv = new List<float>();
        var idx = new List<int>();
        foreach (var (x0, y0, x1, y1) in rects)
        {
            int b = pos.Count / 3;
            foreach (var (x, y) in new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) })
            {
                pos.AddRange(new[] { x, y, 0.0 });
                uv.AddRange(new[] { (float)(x / 10), (float)(y / 10) });
            }
            idx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        return new AgrGeomPrimitive { Mesh = 0, Material = 0, Positions = pos.ToArray(), RawPositions = pos.ToArray(), Uvs = uv.ToArray(), Indices = idx.ToArray() };
    }

    static AgrLevelResult Level(string name, bool simplified = true) => new() { Name = name, Flags = new List<string>(), Simplified = simplified };

    static void Measure(AgrLevelResult lr, AgrGeomPrimitive src, AgrGeomPrimitive level, double texel)
    {
        var s = new[] { src };
        AgrPartConverter.MeasureGeometry(lr, AgrConvertOptions.DefaultLevels.First(l => l.Name == lr.Name), new[] { level },
            new AgrTriangleTree(s), AgrMeshSampling.UniqueUsedVertices(s), texel, Opt);
    }

    [Fact]
    public void Reverse_HoleClosedByLevel_FoundByAreaPoints_NotByVertices()
    {
        // Исходник — рамка 10×10 м с дырой 6×6 м; уровень — сплошной квадрат. Вершины уровня лежат на исходнике,
        // вершины исходника — на уровне: ошибку (до 3 м в центре дыры) видят только точки по площади (о).
        var frame = Quads((0, 0, 10, 2), (0, 8, 10, 10), (0, 2, 2, 8), (8, 2, 10, 8));
        var lr = Level("L0");
        Measure(lr, frame, Quads((0, 0, 10, 10)), texel: 0.03);
        Assert.True(lr.Forward!.Max < 1e-9, "прямая: вершины исходника на уровне");
        Assert.True(lr.ReverseVerticesOnlyMax < 1e-9, "обратная по вершинам: вершины уровня на исходнике");
        Assert.InRange(lr.Reverse!.Max, 2.8, 3.0);
        Assert.True(lr.Reverse.Points > 20_000, "обратная: вершины и точки по площади");
        Assert.Equal(Math.Round(lr.Reverse.Max, 5), lr.GeomM);
        Assert.Equal(Math.Round(lr.Reverse.Max, 4), lr.GeometricError); // L0 не лист: max(геометрия, тексель)
    }

    [Fact]
    public void Forward_LevelDropsHalfOfSource_ErrorFromSourceVertices()
    {
        // Уровень — половина квадрата: вершины и площадь уровня на исходнике, а угол исходника (0, 10) — в 7,07 м от уровня (к).
        var level = new AgrGeomPrimitive
        {
            Mesh = 0, Positions = new double[] { 0, 0, 0, 10, 0, 0, 10, 10, 0 }, RawPositions = new double[] { 0, 0, 0, 10, 0, 0, 10, 10, 0 },
            Indices = new[] { 0, 1, 2 },
        };
        var lr = Level("L1");
        Measure(lr, Quads((0, 0, 10, 10)), level, texel: 0.0078);
        Assert.Equal(10 / Math.Sqrt(2), lr.Forward!.Max, 9);
        Assert.True(lr.Reverse!.Max < 1e-9);
        Assert.Equal(Math.Round(10 / Math.Sqrt(2), 5), lr.GeomM);
        Assert.Equal(Math.Round(10 / Math.Sqrt(2), 4), lr.GeometricError);
    }

    [Fact]
    public void GeometricError_TexelWinsOnCoarseLevel_LeafIsZero_TexelKeptToMicrometres()
    {
        var sq = Quads((0, 0, 10, 10));
        var l0 = Level("L0");
        Measure(l0, sq, sq, texel: 10.0 / 256); // геометрия совпала — geometricError L0 задаёт тексель
        Assert.True(l0.GeomM < 1e-9);
        Assert.Equal(Math.Round(10.0 / 256, 6), l0.TexelM);
        Assert.Equal(0.0391, l0.GeometricError);

        var l1 = Level("L1");
        Measure(l1, sq, sq, texel: 1.0 / 1024);
        Assert.Equal(0.000977, l1.TexelM); // (с): не грубее 0,01 мм
        Assert.Equal(0.001, l1.GeometricError);

        var l2 = Level("L2", simplified: false);
        Measure(l2, sq, Quads((0, 0, 10, 9.99)), texel: 1.0 / 2048);
        Assert.True(l2.GeomM > 0.009);
        Assert.Equal(0, l2.GeometricError); // лист
        Assert.Equal(0.000488, l2.TexelM);
    }

    [Fact]
    public void Texel_BySideOfLevelImage_NotByLevelLimit()
    {
        var sq = new[] { Quads((0, 0, 10, 10)) }; // 10 м на единицу UV
        Assert.Equal(10.0, AgrMeshSampling.MetersPerUv(sq, _ => true), 9);
        Assert.Equal(10.0 / 256, AgrMeshSampling.Texel(sq, _ => 256), 12);
        Assert.Equal(10.0 / 2048, AgrMeshSampling.Texel(sq, _ => 2048), 12);
        Assert.Equal(0, AgrMeshSampling.Texel(sq, _ => 0)); // нет картинки — нет текселя
        // два материала по площади 1:3 — средневзвешенно
        var a = Quads((0, 0, 10, 10));
        var b = new AgrGeomPrimitive { Mesh = 1, Material = 1, Positions = a.Positions.Select((v, i) => i % 3 == 2 ? v : v * Math.Sqrt(3)).ToArray(), RawPositions = a.Positions, Uvs = a.Uvs, Indices = a.Indices };
        double t = AgrMeshSampling.Texel(new[] { a, b }, m => m == 0 ? 256 : 1024);
        Assert.Equal((1 * 10.0 / 256 + 3 * 10 * Math.Sqrt(3) / 1024) / 4, t, 12);
    }

    [Fact]
    public void Degenerate_FourSeparateCounters()
    {
        var p = new AgrGeomPrimitive
        {
            Mesh = 0,
            Positions = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 2, 2, 2, 3, 3, 3 },
            RawPositions = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 2, 2, 2, 3, 3, 3 },
            Indices = new[] { 0, 1, 2, 3, 3, 4, 0, 3, 4, 1, 2, 0 },
        };
        var d = AgrMeshSampling.Degenerate(new[] { p });
        Assert.Equal(new AgrDegenerateStats(4, 1, 1, 2), d);
    }

    static (double[] Min, double[] Max) BoxOf(JsonNode box)
    {
        var b = box.AsArray().Select(v => double.Parse(v!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return (new[] { b[0] - b[3], b[1] - b[7], b[2] - b[11] }, new[] { b[0] + b[3], b[1] + b[7], b[2] + b[11] });
    }

    static bool Inside((double[] Min, double[] Max) inner, (double[] Min, double[] Max) outer) =>
        Enumerable.Range(0, 3).All(i => inner.Min[i] >= outer.Min[i] - 1e-4 && inner.Max[i] <= outer.Max[i] + 1e-4);

    [Fact]
    public void Tileset_TileBoxContainsEveryDescendant()
    {
        // У L0 box меньше, чем у L1 и L2 (упрощение срезало края): box тайла обязан охватить всех потомков.
        var r = new AgrConvertResult { PartName = "SM_Test_001", ZipPath = "x.glb.zip" };
        double[][] mins = { new[] { -5.0, -2, 0 }, new[] { -8.0, -4, -1 }, new[] { -10.0, -5, -2 } };
        double[][] maxs = { new[] { 5.0, 2, 1 }, new[] { 9.0, 4, 2 }, new[] { 10.0, 6, 3 } };
        for (int i = 0; i < 3; i++)
        {
            r.Levels.Add(new AgrLevelResult
            {
                Name = "L" + i, Flags = new List<string>(), GeometricError = 0.3 - i * 0.15, GeomM = 0.1, TexelM = 0.01,
                BoxMinZup = mins[i], BoxMaxZup = maxs[i],
            });
        }
        var part = new AgrPart { Name = "SM_Test_001", FbxFile = "SM_Test_001.fbx", Placement = new AgrPlacement { E = 1, N = 2, DZ = 3 } };
        var ts = AgrPartConverter.BuildTileset(part, "SM_Test_001", r);
        var chain = new List<JsonNode>();
        for (var t = ts["root"]; t != null; t = t["children"]?[0]) chain.Add(t);
        Assert.Equal(4, chain.Count); // корень части + L0, L1, L2
        for (int i = 0; i < chain.Count; i++)
        {
            var outer = BoxOf(chain[i]["boundingVolume"]!["box"]!);
            for (int j = i; j < chain.Count; j++)
            {
                Assert.True(Inside(BoxOf(chain[j]["boundingVolume"]!["box"]!), outer), $"тайл {i} не охватывает тайл {j}");
                if (chain[j]["content"]?["boundingVolume"]?["box"] is JsonNode cb)
                {
                    Assert.True(Inside(BoxOf(cb), outer), $"тайл {i} не охватывает содержимое {j}");
                }
            }
        }
        var l2 = BoxOf(chain[3]["content"]!["boundingVolume"]!["box"]!);
        Assert.Equal(-10.001, l2.Min[0], 3);
        Assert.Equal(6.001, l2.Max[1], 3);
    }

    [Fact]
    public void TextureThreads_ConstantByDefault_NotByCoreCount()
    {
        Assert.Equal(8, AgrConvertOptions.DefaultTextureThreads);
        var (f, _, _) = AgrPartConverter.LevelFlags(AgrConvertOptions.DefaultLevels[0], true, 100, new AgrConvertOptions { GltfpackPath = "g" });
        Assert.Equal("8", f[f.IndexOf("-tj") + 1]);
    }

    // ---- с настоящим gltfpack ----

    static AgrConvertResult Run(string tmp, string tag, Func<AgrConvertOptions, AgrConvertOptions>? tune = null, int threads = 2)
    {
        var opt = new AgrConvertOptions
        {
            GltfpackPath = GltfpackFactAttribute.Exe!, WorkRoot = Path.Combine(tmp, "work-" + tag), TextureThreads = threads,
            MinTrianglesToSimplify = 0,
        };
        return AgrPartConverter.ConvertPart(TestPaths.FixtureDir, Path.Combine(tmp, "out-" + tag), tune?.Invoke(opt) ?? opt);
    }

    static AgrConvertOptions WithLevels(AgrConvertOptions o, double e0, double e1, int attempts = 1) => new()
    {
        GltfpackPath = o.GltfpackPath, WorkRoot = o.WorkRoot, TextureThreads = o.TextureThreads, MinTrianglesToSimplify = 0,
        MaxErrorAttempts = attempts,
        Levels = new[]
        {
            new AgrLevelSpec("L0", 256, SimplifyRatio: 0.1, ErrorM: e0, Permissive: true, LockBorder: true),
            new AgrLevelSpec("L1", 1024, SimplifyRatio: 0.5, ErrorM: e1, LockBorder: true),
            new AgrLevelSpec("L2", 2048),
        },
    };

    static List<string> Geometry(List<string> flags)
    {
        var g = new List<string>(flags);
        g.RemoveRange(g.IndexOf("-tl"), 2);
        return g;
    }

    /// <summary>package_sha256 как у Студии (tiles/package.py): sha256 отсортированных строк «путь\tsha256\tразмер\n».</summary>
    static string StudioPackageSha(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var lines = zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e =>
        {
            using var s = e.Open();
            return $"{e.FullName}\t{Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant()}\t{e.Length}\n";
        }).OrderBy(x => x, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines)))).ToLowerInvariant();
    }

    static string FileSha(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    [GltfpackFact]
    public void ConvertPart_TwoRuns_SameZipAndStudioPackageSha()
    {
        using var tmp = new TempDir("c1c1fix-det");
        var a = Run(tmp.Path, "a");
        var b = Run(tmp.Path, "b");
        Assert.Equal(FileSha(a.ZipPath), FileSha(b.ZipPath));
        Assert.Equal(StudioPackageSha(a.ZipPath), StudioPackageSha(b.ZipPath));
        using var zip = ZipFile.OpenRead(a.ZipPath);
        Assert.All(zip.Entries, e => Assert.Equal(AgrPartConverter.ZipEntryTime.DateTime, e.LastWriteTime.DateTime));
        string man = new StreamReader(zip.GetEntry("manifest.json")!.Open()).ReadToEnd();
        Assert.DoesNotContain("_sec\"", man);
        Assert.Contains("-tj 2", man); // число потоков — из параметров, не из машины
        Assert.True(a.Levels[0].Simplified && a.Levels[0].PackSeconds > 0, "время — в итоге уровня, не в манифесте");
        // тексель — по стороне картинки в GLB: у фикстуры картинки ≤ 1024, поэтому у L1 и L2 он один, хотя пределы 1024 и 2048
        Assert.All(a.Levels, l => Assert.Equal(0, l.TexelPxByLimit));
        Assert.InRange(a.Levels[2].MaxImagePx, 1, 1024);
        Assert.Equal(a.Levels[1].TexelM, a.Levels[2].TexelM);
        Assert.True(a.Levels[2].TexelM > 0);
    }

    [GltfpackFact]
    public void ConvertPart_L0AboveLimit_TakesGeometryOfL1()
    {
        using var tmp = new TempDir("c1c1fix-fb0");
        var r = Run(tmp.Path, "a", o => WithLevels(o, e0: 1e-7, e1: 100));
        var (l0, l1) = (r.Levels[0], r.Levels[1]);
        Assert.Null(l1.Fallback);
        Assert.NotNull(l0.Fallback);
        Assert.Equal("L1", l0.Fallback!.GeometryOf);
        Assert.True(l0.Fallback.ErrorM > 1e-7);
        Assert.Equal(Geometry(l1.Flags), Geometry(l0.Flags));
        Assert.Contains("256", l0.Flags); // текстуры — свои
        Assert.Equal(l1.Triangles, l0.Triangles);
        Assert.Equal(l1.GeomM, l0.GeomM);
        Assert.Contains(r.Warnings, w => w.StartsWith("L0: ошибка") && w.Contains("геометрии L1"));
        var fb = r.Manifest!["parts"]![0]!["levels"]!["L0"]!["simplify"]!["fallback"]!;
        Assert.Equal("L1", fb["geometry_of"]!.GetValue<string>());
        Assert.Null(r.Manifest["parts"]![0]!["levels"]!["L1"]!["simplify"]!["fallback"]);
    }

    [GltfpackFact]
    public void ConvertPart_L1AboveLimit_TakesFullGeometry_NoToleranceOnLimit()
    {
        using var tmp = new TempDir("c1c1fix-fb1");
        var r = Run(tmp.Path, "a", o => WithLevels(o, e0: 100, e1: 1e-7, attempts: 2));
        var l1 = r.Levels[1];
        Assert.Equal(2, l1.Attempts);
        Assert.Equal("L2", l1.Fallback!.GeometryOf);
        Assert.False(l1.Simplified);
        Assert.DoesNotContain("-si", l1.Flags);
        Assert.Equal(r.TrianglesRef, l1.Triangles);
        Assert.Equal(r.TrianglesRef, l1.TrianglesGlb);
        Assert.Null(r.Levels[0].Fallback);
        var deg = r.Manifest!["parts"]![0]!["levels"]!["L1"]!["degenerate"]!;
        Assert.True(deg["balance_ok"]!.GetValue<bool>());
        Assert.Equal(0, deg["dropped_by_gltfpack"]!.GetValue<long>());
    }

    [GltfpackFact]
    public void ConvertPart_SmallPart_NotSimplified()
    {
        using var tmp = new TempDir("c1c1fix-small");
        var r = Run(tmp.Path, "a", o => new AgrConvertOptions
        {
            GltfpackPath = o.GltfpackPath, WorkRoot = o.WorkRoot, TextureThreads = 2, MinTrianglesToSimplify = long.MaxValue,
        });
        Assert.All(r.Levels, l => Assert.False(l.Simplified));
        Assert.All(r.Levels, l => Assert.DoesNotContain("-si", l.Flags));
        Assert.All(r.Levels, l => Assert.Equal(r.TrianglesRef, l.Triangles));
        Assert.All(r.Levels.Take(2), l => Assert.Contains("порога", l.SimplifySkipped));
        Assert.All(r.Levels, l => Assert.Null(l.Fallback));
    }
}
