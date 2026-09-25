using System.IO.Compression;
using System.Text.Json.Nodes;
using Connector.AgrConversion;
using Xunit;

namespace Connector.AgrConversion.Tests;

/// <summary>Прогон с настоящим gltfpack: путь — в переменной AGR_GLTFPACK (в MSI его кладёт C1d).</summary>
public sealed class GltfpackFactAttribute : FactAttribute
{
    public const string Variable = "AGR_GLTFPACK";

    public static string? Exe => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } p && File.Exists(p) ? p : null;

    public GltfpackFactAttribute()
    {
        if (Exe == null)
        {
            Skip = $"нужен gltfpack: задайте {Variable} (путь к gltfpack.exe 1.2)";
        }
    }
}

/// <summary>C1c-1: флаги уровней, tileset части, замер расстояний, пакет zip на фикстуре.</summary>
public class PackTests
{
    static readonly AgrConvertOptions Opt = new() { GltfpackPath = "gltfpack.exe" };

    [Fact]
    public void QuantizationLimit_Is_756_7_m()
    {
        Assert.Equal(756.7, AgrConvertOptions.QuantizationLimit(16, 0.01));
        Assert.Equal(756.7, Opt.QuantizationLimitM);
    }

    [Fact]
    public void DefaultLevels_GoFromCoarseToFull()
    {
        var l = AgrConvertOptions.DefaultLevels;
        Assert.Equal(new[] { "L0", "L1", "L2" }, l.Select(x => x.Name));
        Assert.Equal(new[] { 256, 1024, 2048 }, l.Select(x => x.TextureLimit));
        Assert.True(l[0].SimplifyRatio < l[1].SimplifyRatio && l[1].SimplifyRatio < l[2].SimplifyRatio);
        Assert.False(l[2].Simplifies);
        Assert.True(l[0].ErrorM > l[1].ErrorM && l[1].ErrorM > 0, "предел ошибки в метрах убывает к L2 (требование (н))");
    }

    [Fact]
    public void LevelFlags_Part001_Quantized16_AndMeterError()
    {
        const double extent = 741.5; // габарит 001
        var levels = AgrConvertOptions.DefaultLevels;
        var (f0, t0, se0) = AgrPartConverter.LevelFlags(levels[0], extent <= Opt.QuantizationLimitM, extent, Opt);
        var (f1, _, se1) = AgrPartConverter.LevelFlags(levels[1], extent <= Opt.QuantizationLimitM, extent, Opt);
        var (f2, _, se2) = AgrPartConverter.LevelFlags(levels[2], extent <= Opt.QuantizationLimitM, extent, Opt);
        foreach (var f in new[] { f0, f1, f2 })
        {
            Assert.Contains("-vp", f);
            Assert.DoesNotContain("-noq", f);
            Assert.Contains("-cc", f);
            Assert.Contains("-tc", f);
        }
        Assert.Equal("256", f0[f0.IndexOf("-tl") + 1]);
        Assert.Equal("1024", f1[f1.IndexOf("-tl") + 1]);
        Assert.Equal("2048", f2[f2.IndexOf("-tl") + 1]);
        Assert.Equal(0.5 / extent, se0!.Value, 12);
        Assert.Equal(0.15 / extent, se1!.Value, 12);
        Assert.Null(se2);
        Assert.Contains("-sp", f0);
        Assert.Contains("-slb", f0);
        Assert.DoesNotContain("-si", f2);
        // двойник: те же флаги геометрии, без сжатия и текстур
        Assert.DoesNotContain("-cc", t0);
        Assert.DoesNotContain("-tc", t0);
        Assert.Contains("-se", t0);
        Assert.Equal(f0[f0.IndexOf("-se") + 1], t0[t0.IndexOf("-se") + 1]);
    }

    [Fact]
    public void LevelFlags_Ground_NoQuantization_ErrorNotRelativeToExtent()
    {
        const double extent = 4426.9; // габарит Ground
        foreach (var spec in AgrConvertOptions.DefaultLevels)
        {
            var (f, t, se) = AgrPartConverter.LevelFlags(spec, extent <= Opt.QuantizationLimitM, extent, Opt);
            Assert.Contains("-noq", f);
            Assert.Contains("-noq", t);
            Assert.DoesNotContain("-vp", f);
            if (spec.Simplifies)
            {
                Assert.Equal(spec.ErrorM, se!.Value * extent, 9); // в метрах — ровно предел уровня
            }
        }
    }

    static AgrConvertResult FakeResult()
    {
        var r = new AgrConvertResult { PartName = "SM_Test_001", ZipPath = "x.glb.zip" };
        double[] ge = { 0.6, 0.2, 0 };
        for (int i = 0; i < 3; i++)
        {
            r.Levels.Add(new AgrLevelResult
            {
                Name = "L" + i, Flags = new List<string>(), GeometricError = ge[i], GeomM = ge[i] == 0 ? 0.004 : ge[i], TexelM = 0.01 / (i + 1),
                BoxMinZup = new[] { -10.0, -5, 0 }, BoxMaxZup = new[] { 10.0, 5, 3 },
            });
        }
        return r;
    }

    static AgrPart FakePart() => new()
    {
        Name = "SM_Test_001", FbxFile = "SM_Test_001.fbx",
        Placement = new AgrPlacement { E = 23613.2, N = 15326.299, DZ = 128.28 },
    };

    [Fact]
    public void Tileset_PartRootWithTransform_ChainL0L1L2_ExtrasEverywhere()
    {
        var ts = AgrPartConverter.BuildTileset(FakePart(), "SM_Test_001", FakeResult());
        Assert.Equal("1.1", ts["asset"]!["version"]!.GetValue<string>());
        var root = ts["root"]!;
        var tr = root["transform"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
        Assert.Equal(new[] { 23613.2, 15326.299, 128.28 }, tr[12..15]);
        Assert.Equal("agr:SM_Test_001", root["metadata"]!["properties"]!["code"]!.GetValue<string>());
        var tile = root["children"]![0];
        var names = new List<string>();
        double prevGe = double.MaxValue;
        while (tile != null)
        {
            string uri = tile["content"]!["uri"]!.GetValue<string>();
            Assert.DoesNotContain("\\", uri);
            Assert.StartsWith("parts/SM_Test_001/", uri);
            names.Add(Path.GetFileNameWithoutExtension(uri));
            Assert.Equal("REPLACE", tile["refine"]!.GetValue<string>());
            var agr = tile["extras"]?["agr"];
            Assert.NotNull(agr);
            Assert.True(agr!["geom_m"]!.GetValue<double>() >= 0);
            Assert.True(agr["texel_m"]!.GetValue<double>() > 0);
            double ge = tile["geometricError"]!.GetValue<double>();
            Assert.True(ge <= prevGe);
            prevGe = ge;
            tile = tile["children"]?[0];
        }
        Assert.Equal(new[] { "L0", "L1", "L2" }, names);
        Assert.Equal(0, prevGe);
    }

    [Fact]
    public void TriangleTree_DistanceAndDegenerates()
    {
        var prim = new AgrGeomPrimitive
        {
            Mesh = 0,
            Positions = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 2, 2, 2, 2, 2, 2, 3, 3, 3, 0, 0, 5, 1, 1, 5, 2, 2, 5 },
            RawPositions = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 2, 2, 2, 2, 2, 2, 3, 3, 3, 0, 0, 5, 1, 1, 5, 2, 2, 5 },
            Indices = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 },
        };
        var tree = new AgrTriangleTree(new[] { new AgrGeomPrimitive { Mesh = 0, Positions = prim.Positions[..9], RawPositions = prim.Positions[..9], Indices = new[] { 0, 1, 2 } } });
        Assert.Equal(2.0, tree.Distance(0.2, 0.2, 2), 12);
        Assert.Equal(Math.Sqrt(2), tree.Distance(2, 1, 0) , 12);
        var d = AgrMeshSampling.Degenerate(new[] { prim });
        Assert.Equal(3, d.Triangles);
        Assert.Equal(1, d.Coincident);
        Assert.Equal(1, d.Collinear);
        var pts = AgrMeshSampling.AreaSamples(new[] { prim }, 1000, 7);
        Assert.Equal(3000, pts.Length);
        Assert.All(Enumerable.Range(0, 1000), k => Assert.True(tree.Distance(pts[k * 3], pts[k * 3 + 1], pts[k * 3 + 2]) < 1e-9));
    }

    [GltfpackFact]
    public void ConvertPart_Fixture_ZipWithTilesetManifestAndLevels()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "agr-c1c1-test-" + Guid.NewGuid().ToString("N")[..8]);
        string work = Path.Combine(tmp, "work");
        try
        {
            var opt = new AgrConvertOptions { GltfpackPath = GltfpackFactAttribute.Exe!, WorkRoot = work, TextureThreads = 2, MinTrianglesToSimplify = 0 };
            var r = AgrPartConverter.ConvertPart(TestPaths.FixtureDir, Path.Combine(tmp, "out"), opt);
            Assert.EndsWith("SM_TestPart_001.glb.zip", r.ZipPath);
            Assert.True(r.Quantized);
            Assert.Empty(Directory.EnumerateFileSystemEntries(work)); // временная папка удалена
            Assert.Equal(new[] { "L0", "L1", "L2" }, r.Levels.Select(l => l.Name));
            Assert.True(r.Levels[0].Triangles <= r.Levels[1].Triangles && r.Levels[1].Triangles <= r.Levels[2].Triangles);
            Assert.Equal(r.TrianglesRef, r.Levels[2].Triangles);
            Assert.All(r.Levels, l => Assert.True(l.TwinMatches, l.Name + ": двойник не совпал"));
            Assert.All(r.Levels, l => Assert.Contains("EXT_meshopt_compression", l.ExtensionsUsed));
            Assert.All(r.Levels, l => Assert.True(l.Images == l.Ktx2Images && l.Images > 0,
                $"{l.Name}: KTX2 {l.Ktx2Images}/{l.Images}; {string.Join(" | ", r.Warnings)}"));
            Assert.True(r.Levels[0].MaxImagePx <= 256);
            Assert.Equal(0, r.Levels[2].GeometricError);
            Assert.True(r.Levels[2].GeomM < 0.01, "L2: ошибка квантования ≤ 1 см");

            using var zip = ZipFile.OpenRead(r.ZipPath);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();
            Assert.Contains("tileset.json", names);
            Assert.Contains("manifest.json", names);
            Assert.All(names, n => Assert.DoesNotContain("\\", n));
            var ts = JsonNode.Parse(zip.GetEntry("tileset.json")!.Open())!;
            var uris = new List<string>();
            var tile = ts["root"];
            while (tile != null)
            {
                if (tile["content"] is JsonNode c)
                {
                    uris.Add(c["uri"]!.GetValue<string>());
                    Assert.NotNull(tile["extras"]?["agr"]?["geom_m"]);
                }
                tile = tile["children"]?[0];
            }
            Assert.Equal(new[] { "parts/SM_TestPart_001/L0.glb", "parts/SM_TestPart_001/L1.glb", "parts/SM_TestPart_001/L2.glb" }, uris);
            Assert.All(uris, u => Assert.Contains(u, names));
            Assert.Equal(names.Count, uris.Count + 2); // лишних файлов нет
            var man = JsonNode.Parse(zip.GetEntry("manifest.json")!.Open())!;
            Assert.Equal("agr:SM_TestPart_001", man["parts"]![0]!["code"]!.GetValue<string>());
            Assert.Equal("m", man["extras_agr_schema"]!["texel_m"]!["unit"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    [GltfpackFact]
    public void ConvertPart_LargerThanLimit_UsesFloatPositions()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "agr-c1c1-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var opt = new AgrConvertOptions
            {
                GltfpackPath = GltfpackFactAttribute.Exe!, WorkRoot = Path.Combine(tmp, "work"), TextureThreads = 2,
                QuantizationLimitM = 0.01, // фикстура «крупнее» предела → -noq
            };
            var r = AgrPartConverter.ConvertPart(TestPaths.FixtureDir, Path.Combine(tmp, "out"), opt);
            Assert.False(r.Quantized);
            Assert.All(r.Levels, l => Assert.Contains("-noq", l.Flags));
            Assert.All(r.Levels, l => Assert.DoesNotContain("KHR_mesh_quantization", l.ExtensionsUsed));
            Assert.True(r.Levels[2].GeomM < 1e-4, "float32 без квантования: ошибка позиций L2 < 0,1 мм");
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }
}

/// <summary>Замечание ревью C1b: однотонная по цвету Diffuse с непостоянной альфой не сворачивается в константу.</summary>
public class UniformAlphaTests
{
    [Fact]
    public void UniformDiffuseWithVaryingAlpha_StaysImage()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "agr-c1c1-alpha-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string dir = Path.Combine(tmp, TestPaths.FixturePart);
            TestPaths.CopyDirectory(TestPaths.FixtureDir, dir);
            PngTestWriter.Write(Path.Combine(dir, "T_TestPart_001_Diffuse_1.1002.png"), 8, 8, 4, 8, (x, y) => new[] { 100, 150, 200, (x + y * 8) * 4 });
            var part = new AgrPartReader().Read(dir);
            var tex = part.Textures.Single(t => t.File == "T_TestPart_001_Diffuse_1.1002.png");
            Assert.True(tex.Uniform, "цвет картинки однотонный");
            Assert.True(tex.Alpha is { NonTrivial: true }, "альфа непостоянная");
            var res = new AgrGltfWriter(part, dir).Write(Path.Combine(tmp, "gltf"));
            var mats = res.Materials.Where(m => m.Tile == 1002 && m.Class != "glass").ToList();
            Assert.NotEmpty(mats);
            Assert.All(mats, m => Assert.StartsWith("image ", m.Maps["Diffuse"]));
            Assert.All(mats, m => Assert.NotEqual("OPAQUE", m.AlphaMode));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }
}

public class LongPathTests
{
    [GltfpackFact]
    public void SameVolume_LongLink_CopiedNextToGltf_GltfpackKeepsEveryImage()
    {
        // Ссылка «папка .gltf + ../…» длиннее MAX_PATH: gltfpack молча пропускал бы картинки («error reading source file»).
        // Тот же том, но ссылка > 240 символов → картинки копируются рядом с .gltf, и gltfpack кодирует все в KTX2.
        using var tmp = new TempDir("c1c1-long");
        var names = Directory.GetFiles(TestPaths.FixtureDir, "*.png").Select(f => Path.GetFileName(f)).ToList();
        int n = 250 - (tmp.Path.Length + 2 + names.Max(x => x.Length));
        Assert.InRange(n, 20, 200);
        string src = Path.Combine(tmp.Path, new string('d', n));
        TestPaths.CopyDirectory(TestPaths.FixtureDir, src);
        string outDir = Path.Combine(new[] { tmp.Path, "o" }.Concat(Enumerable.Range(0, 20).Select(i => ((char)('a' + i)).ToString())).ToArray());
        string linked = Path.Combine(outDir, Path.GetRelativePath(outDir, Path.Combine(src, names[0])));
        Assert.True(linked.Length > 260, "ссылка должна быть длиннее MAX_PATH: " + linked.Length);
        Assert.True(src.Length + 1 + names.Max(x => x.Length) < 260, "исходник открывается без длинных путей");

        var part = new AgrPartReader().Read(src);
        var res = new AgrGltfWriter(part, src).Write(outDir);
        Assert.Equal(5, res.Images.Count);
        Assert.All(res.Images, i => Assert.True(i.Copied, i.File + ": на том же томе длинная ссылка должна стать копией"));
        Assert.All(res.Images, i => Assert.DoesNotContain("..", i.Uri));

        string glb = Path.Combine(outDir, "t.glb");
        var psi = new System.Diagnostics.ProcessStartInfo(GltfpackFactAttribute.Exe!)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-i", res.GltfPath, "-o", glb, "-tc", "-tj", "2" }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string log = p.StandardOutput.ReadToEnd() + err.Result;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        Assert.DoesNotContain("unable to encode image", log);
        var images = AgrGlbGeometry.Load(glb).Images();
        Assert.Equal(res.Images.Count, images.Count(i => i.Mime == "image/ktx2"));
    }
}
