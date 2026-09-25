using System.Numerics;
using System.Text.Json.Nodes;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;

namespace Connector.AgrConversion.Tests;

/// <summary>
/// Запись промежуточного glTF (C1b) на фикстуре: запись → чтение обратно SharpGLTF со строгой проверкой → сверка
/// с <see cref="AgrPart"/> (позиции, нормали, UV, индексы — точно), samplers, URI картинок (кириллица, пробел, «#», «%»,
/// другой том), материалы S1a-1 (текстуры, заглушки, альфа, стекло), параллельная запись.
/// </summary>
public class GltfWriterTests
{
    const string MainMat = "M_TestPart_001_Main_1";
    const string GlassMat = "M_TestPart_001_MainGlass_1";

    static AgrPart ReadFixture(string? dir = null, bool analyze = true) =>
        new AgrPartReader(new AgrReadOptions { AnalyzeTextures = analyze }).Read(dir ?? TestPaths.FixtureDir);

    static ModelRoot LoadStrict(string gltf) => ModelRoot.Load(gltf, new ReadSettings { Validation = ValidationMode.Strict });

    static JsonNode Json(string gltf) => JsonNode.Parse(File.ReadAllText(gltf))!;

    static Dictionary<string, JsonObject> Materials(JsonNode g) =>
        g["materials"]!.AsArray().ToDictionary(m => (string)m!["name"]!, m => m!.AsObject(), StringComparer.Ordinal);

    static float Lin(int c255)
    {
        double c = c255 / 255.0;
        return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
    }

    /// <summary>Имя файла картинки канала (decoded URI) или null, если текстуры нет.</summary>
    static string? TextureFile(JsonNode g, JsonObject mat, string channel)
    {
        JsonNode? tex = channel == "normalTexture" ? mat["normalTexture"] : mat["pbrMetallicRoughness"]?[channel];
        if (tex == null)
        {
            return null;
        }
        int source = (int)g["textures"]![(int)tex["index"]!]!["source"]!;
        return Path.GetFileName(Uri.UnescapeDataString((string)g["images"]![source]!["uri"]!));
    }

    static float Pbr(JsonObject mat, string key, float dflt) =>
        mat["pbrMetallicRoughness"]?[key] is JsonNode v ? (float)v : dflt;

    static string TexFile(AgrPart part, string kind, int tile) => part.Textures.Single(t => t.Kind == kind && t.Tile == tile).File;

    [Fact]
    public void Fixture_RoundTrip_PositionsNormalsUvIndicesExact_InYUp()
    {
        var part = ReadFixture();
        using var tmp = new TempDir("c1b-rt");
        var res = new AgrGltfWriter(part, TestPaths.FixtureDir).Write(tmp.Path);
        var model = LoadStrict(res.GltfPath);

        var root = model.DefaultScene.VisualChildren.Single();
        Assert.Equal(part.Name, root.Name);
        Assert.Null(root.Mesh);
        var nodes = part.Meshes.Select(m => m.Node).Distinct().ToList();
        Assert.Equal(nodes, root.VisualChildren.Select(n => n.Name).ToList());
        Assert.All(model.LogicalNodes, n => Assert.True(n.LocalMatrix.IsIdentity, n.Name + ": у узла есть TRS"));

        long triangles = 0;
        int primitives = 0;
        foreach (var node in root.VisualChildren)
        {
            foreach (var prim in node.Mesh.Primitives)
            {
                primitives++;
                string material = prim.Material.Name;
                var pos = prim.GetVertexAccessor("POSITION").AsVector3Array();
                var nrm = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var uv = prim.GetVertexAccessor("TEXCOORD_0").AsVector2Array();
                var idx = prim.GetIndices();
                var expected = new List<(Vector3 P, Vector3? N, Vector2 T)>();
                foreach (var m in part.Meshes.Where(m => m.Node == node.Name))
                {
                    for (int t = 0; t < m.TriangleCount; t++)
                    {
                        if (m.Material + "_" + m.FaceTiles[t] != material)
                        {
                            continue;
                        }
                        int tu = 0, tv = 0;
                        if (m.Uvs is { } u)
                        {
                            int a = m.Indices[t * 3], b = m.Indices[t * 3 + 1], c = m.Indices[t * 3 + 2];
                            tu = (int)Math.Floor(((double)u[a * 2] + u[b * 2] + u[c * 2]) / 3.0);
                            tv = (int)Math.Floor(((double)u[a * 2 + 1] + u[b * 2 + 1] + u[c * 2 + 1]) / 3.0);
                        }
                        for (int c = 0; c < 3; c++)
                        {
                            int v = m.Indices[t * 3 + c];
                            var p = new Vector3(m.Positions[v * 3], m.Positions[v * 3 + 2], -m.Positions[v * 3 + 1]);
                            Vector3? n = m.Normals is { } nn ? new Vector3(nn[v * 3], nn[v * 3 + 2], -nn[v * 3 + 1]) : null;
                            var st = m.Uvs is { } uu
                                ? new Vector2((float)(uu[v * 2] - (double)tu), (float)(1.0 - (uu[v * 2 + 1] - (double)tv)))
                                : new Vector2(0f, 1f);
                            expected.Add((p, n, st));
                        }
                    }
                }
                Assert.True(expected.Count > 0, material + ": примитив без граней в разборе");
                Assert.Equal(expected.Count, idx.Count);
                for (int i = 0; i < idx.Count; i++)
                {
                    int k = (int)idx[i];
                    Assert.Equal(expected[i].P, pos[k]);
                    if (expected[i].N is { } en)
                    {
                        Assert.Equal(en, nrm![k]);
                    }
                    Assert.Equal(expected[i].T, uv[k]);
                }
                triangles += idx.Count / 3;
            }
        }
        Assert.Equal(part.ModelTriangles, triangles);
        Assert.Equal(res.Primitives, primitives);
        Assert.Equal(part.ModelTriangles, res.Triangles);

        // Y вверх: x_gltf = восток, y_gltf = высота, z_gltf = −север
        var all = model.LogicalMeshes.SelectMany(m => m.Primitives)
                       .SelectMany(p => p.GetVertexAccessor("POSITION").AsVector3Array()).ToList();
        var b0 = part.Bounds;
        Assert.Equal(b0.Min[0], all.Min(p => p.X), 5);
        Assert.Equal(b0.Max[0], all.Max(p => p.X), 5);
        Assert.Equal(b0.Min[2], all.Min(p => p.Y), 5);
        Assert.Equal(b0.Max[2], all.Max(p => p.Y), 5);
        Assert.Equal(-b0.Max[1], all.Min(p => p.Z), 5);
        Assert.Equal(-b0.Min[1], all.Max(p => p.Z), 5);
    }

    [Fact]
    public void Fixture_AllTexturesUseOneClampToEdgeSampler()
    {
        var part = ReadFixture();
        using var tmp = new TempDir("c1b-smp");
        var res = new AgrGltfWriter(part, TestPaths.FixtureDir).Write(tmp.Path);
        var g = Json(res.GltfPath);
        var samplers = g["samplers"]!.AsArray();
        Assert.Single(samplers);
        Assert.Equal(33071, (int?)samplers[0]!["wrapS"]);
        Assert.Equal(33071, (int?)samplers[0]!["wrapT"]);
        Assert.Equal(9729, (int?)samplers[0]!["magFilter"]);
        Assert.Equal(9987, (int?)samplers[0]!["minFilter"]);
        var textures = g["textures"]!.AsArray();
        Assert.Equal(5, textures.Count);
        Assert.All(textures, t => Assert.Equal(0, (int?)t!["sampler"]));

        var model = LoadStrict(res.GltfPath);
        Assert.All(model.LogicalTextures, t =>
        {
            Assert.Equal(TextureWrapMode.CLAMP_TO_EDGE, t.Sampler.WrapS);
            Assert.Equal(TextureWrapMode.CLAMP_TO_EDGE, t.Sampler.WrapT);
        });
    }

    [Fact]
    public void Fixture_MaterialsAsS1a1_TexturesStubsMaskAndGlass()
    {
        var part = ReadFixture();
        using var tmp = new TempDir("c1b-mat");
        var res = new AgrGltfWriter(part, TestPaths.FixtureDir).Write(tmp.Path);
        var g = Json(res.GltfPath);
        var mats = Materials(g);
        Assert.Equal(new[] { $"{GlassMat}_1001", $"{MainMat}_1001", $"{MainMat}_1002", $"{MainMat}_1011" },
                     mats.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.All(mats.Values, m => Assert.True((bool)m["doubleSided"]!));
        Assert.Equal(5, g["images"]!.AsArray().Count);

        // 1001: все три карты настоящие
        var m1 = mats[$"{MainMat}_1001"];
        Assert.Null(m1["alphaMode"]);
        Assert.Equal(TexFile(part, "Diffuse", 1001), TextureFile(g, m1, "baseColorTexture"));
        Assert.Equal(TexFile(part, "ERM", 1001), TextureFile(g, m1, "metallicRoughnessTexture"));
        Assert.Equal(TexFile(part, "Normal", 1001), TextureFile(g, m1, "normalTexture"));
        Assert.Null(m1["pbrMetallicRoughness"]!["baseColorFactor"]);
        Assert.Equal(1f, Pbr(m1, "metallicFactor", 1f));
        Assert.Equal(1f, Pbr(m1, "roughnessFactor", 1f));
        Assert.Equal(1001, (int)m1["extras"]!["agr"]!["udim_tile"]!);
        Assert.Equal("real", (string?)m1["extras"]!["agr"]!["class"]);

        // 1002: Diffuse настоящая, ERM-заглушка (255; 101; 0) → шероховатость 101/255, металличность 0; Normal-заглушка → без карты
        var m2 = mats[$"{MainMat}_1002"];
        Assert.Null(m2["alphaMode"]);
        Assert.Equal(TexFile(part, "Diffuse", 1002), TextureFile(g, m2, "baseColorTexture"));
        Assert.Null(TextureFile(g, m2, "metallicRoughnessTexture"));
        Assert.Null(TextureFile(g, m2, "normalTexture"));
        Assert.Equal(101 / 255f, Pbr(m2, "roughnessFactor", 1f), 5);
        Assert.Equal(0f, Pbr(m2, "metallicFactor", 1f));

        // 1011: однотонная Diffuse (200; 50; 25) с альфой 0 в верхней половине — по ревью C1b остаётся картинкой (константа
        // потеряла бы альфу) → MASK; ERM-заглушка (0; 128; 0)
        var m3 = mats[$"{MainMat}_1011"];
        Assert.Equal("MASK", (string?)m3["alphaMode"]);
        Assert.Equal("T_TestPart_001_Diffuse_1.1011.png", TextureFile(g, m3, "baseColorTexture"));
        Assert.Equal("stub", (string?)m3["extras"]!["agr"]!["class"]);
        Assert.Equal(128 / 255f, Pbr(m3, "roughnessFactor", 1f), 5);
        Assert.Equal(0f, Pbr(m3, "metallicFactor", 1f));
        Assert.Null(TextureFile(g, m3, "normalTexture"));

        // стекло: константы паспорта Glasses (175/186/187, transparency 1 → непрозрачное), без текстур
        var gl = mats[$"{GlassMat}_1001"];
        Assert.Null(gl["alphaMode"]);
        var f = gl["pbrMetallicRoughness"]!["baseColorFactor"]!.AsArray().Select(x => (float)x!).ToArray();
        Assert.Equal(new[] { Lin(175), Lin(186), Lin(187), 1f }, f);
        Assert.Equal(0.82f, Pbr(gl, "metallicFactor", 1f), 5);
        Assert.Equal(0.859f, Pbr(gl, "roughnessFactor", 1f), 5);
        Assert.Null(TextureFile(g, gl, "baseColorTexture"));
        Assert.Null(TextureFile(g, gl, "metallicRoughnessTexture"));
        Assert.Null(TextureFile(g, gl, "normalTexture"));
        Assert.Equal("glass", (string?)gl["extras"]!["agr"]!["class"]);
        Assert.Null(g["extensionsUsed"]);
    }

    [Fact]
    public void Glass_TransparencyBelowOne_Blend_WithBlenderFactors()
    {
        var part = ReadFixture();
        var entry = part.Passport!.Glasses.Values.Single();
        // стекло части 008 из S1a-1: sRGB 202/210/223, transparency 0,1 → baseColorFactor Blender
        entry["color_RGB"] = new JsonObject { ["Red"] = 202, ["Green"] = 210, ["Blue"] = 223 };
        entry["transparency"] = 0.1;
        entry["roughness"] = 0.1;
        entry["metallicity"] = 0.1;
        using var tmp = new TempDir("c1b-glass");
        var res = new AgrGltfWriter(part, TestPaths.FixtureDir).Write(tmp.Path);
        var g = Json(res.GltfPath);
        var gl = Materials(g)[$"{GlassMat}_1001"];
        Assert.Equal("BLEND", (string?)gl["alphaMode"]);
        var f = gl["pbrMetallicRoughness"]!["baseColorFactor"]!.AsArray().Select(x => (double)x!).ToArray();
        double[] blender = { 0.5906188488006592, 0.6444796919822693, 0.7379103899002075, 0.1 };
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(blender[i], f[i], 6);
        }
        Assert.Equal(0.1f, Pbr(gl, "metallicFactor", 1f), 5);
        Assert.Equal(0.1f, Pbr(gl, "roughnessFactor", 1f), 5);
        Assert.Null(TextureFile(g, gl, "baseColorTexture"));
        Assert.Equal("BLEND", res.Materials.Single(m => m.Class == "glass").AlphaMode);
        Assert.Contains(res.Notes, n => n.Contains("refraction", StringComparison.Ordinal));
        Assert.Null(g["extensionsUsed"]);
        LoadStrict(res.GltfPath);
    }

    [Fact]
    public void DiffuseAlpha_GradientBlend_HardEdgeMask()
    {
        using var tmp = new TempDir("c1b-alpha");
        string src = Path.Combine(tmp.Path, TestPaths.FixturePart);
        TestPaths.CopyDirectory(TestPaths.FixtureDir, src);
        var probe = ReadFixture(src);
        string d1011 = Path.Combine(src, probe.Textures.Single(t => t.Kind == "Diffuse" && t.Tile == 1011).RelativePath);
        string d1002 = Path.Combine(src, probe.Textures.Single(t => t.Kind == "Diffuse" && t.Tile == 1002).RelativePath);
        // 1011: цвет неоднотонный, альфа — плавный градиент (промежуточных ~94 %) → BLEND
        PngTestWriter.Write(d1011, 32, 32, 4, 8, (x, y) => new[] { 200, 50 + x, 25, x * 255 / 31 });
        // 1002: цвет неоднотонный, альфа только 0 и 255 (промежуточных нет) → MASK, cutoff 0,5
        PngTestWriter.Write(d1002, 32, 32, 4, 8, (x, y) => new[] { 90 + x, 120, 150, y < 16 ? 0 : 255 });

        var part = ReadFixture(src);
        var res = new AgrGltfWriter(part, src).Write(Path.Combine(tmp.Path, "out"));
        var g = Json(res.GltfPath);
        var mats = Materials(g);
        var blend = mats[$"{MainMat}_1011"];
        Assert.Equal("BLEND", (string?)blend["alphaMode"]);
        Assert.NotNull(TextureFile(g, blend, "baseColorTexture"));
        var mask = mats[$"{MainMat}_1002"];
        Assert.Equal("MASK", (string?)mask["alphaMode"]);
        Assert.Equal(0.5f, mask["alphaCutoff"] is JsonNode cut ? (float)cut : 0.5f);
        Assert.NotNull(TextureFile(g, mask, "baseColorTexture"));
        Assert.Equal(new[] { "OPAQUE", "OPAQUE", "MASK", "BLEND" },
                     res.Materials.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => m.AlphaMode).ToArray());
        LoadStrict(res.GltfPath);
    }

    [Fact]
    public void Uri_CyrillicSpaceHashPercent_EncodedPerSegment_ResolveToSources()
    {
        using var tmp = new TempDir("c1b-uri");
        string src = Path.Combine(tmp.Path, "исходники #1 50% a+b", TestPaths.FixturePart);
        TestPaths.CopyDirectory(TestPaths.FixtureDir, src);
        var part = ReadFixture(src);
        string outDir = Path.Combine(tmp.Path, "вывод glTF");
        var res = new AgrGltfWriter(part, src).Write(outDir);

        var uris = Json(res.GltfPath)["images"]!.AsArray().Select(i => (string)i!["uri"]!).ToList();
        Assert.Equal(5, uris.Count);
        foreach (var uri in uris)
        {
            Assert.StartsWith("../%D0%B8%D1%81%D1%85", uri, StringComparison.Ordinal);
            Assert.Contains("%20%231%2050%25%20a%2Bb/" + TestPaths.FixturePart + "/", uri, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", uri, StringComparison.Ordinal);
            Assert.DoesNotContain("#", uri, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", uri, StringComparison.Ordinal);
            Assert.True(uri.All(c => c < 128), uri);
            string full = Path.GetFullPath(Path.Combine(outDir, Uri.UnescapeDataString(uri)));
            Assert.StartsWith(src + Path.DirectorySeparatorChar, full, StringComparison.Ordinal);
            Assert.True(File.Exists(full), full);
        }
        Assert.All(res.Images, i => Assert.False(i.Copied));
        Assert.Empty(Directory.GetFiles(outDir, "*.png"));

        // SharpGLTF (строгая проверка) находит картинки по закодированным URI: байты = исходник
        var model = LoadStrict(res.GltfPath);
        Assert.Equal(5, model.LogicalImages.Count);
        foreach (var img in model.LogicalImages)
        {
            var info = res.Images.Single(i => i.File == img.Name);
            Assert.Equal(File.ReadAllBytes(info.Source), img.Content.Content.ToArray());
        }
    }

    [Fact]
    public void Uri_EncodingAndFileStem_Units()
    {
        Assert.Equal("../a%20b/%D1%87%23%25.png", AgrGltfWriter.EncodeRelativeUri(@"..\a b\ч#%.png"));
        Assert.Equal("SM_Part-1.x", AgrGltfWriter.FileStem("SM_Part-1.x"));
        string stem = AgrGltfWriter.FileStem("ч асть #1%");
        Assert.Matches("^[A-Za-z0-9._-]+$", stem);
        Assert.NotEqual(AgrGltfWriter.FileStem("a b"), AgrGltfWriter.FileStem("a_b"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SameFileNameInSubfolders_KeptApart(bool sameVolume)
    {
        // Одноимённые картинки в разных подпапках: ключ — путь внутри части, копии и ссылки не перетирают друг друга.
        using var tmp = new TempDir("c1c1-samename");
        string src = Path.Combine(tmp.Path, "s");
        TestPaths.CopyDirectory(TestPaths.FixtureDir, src);
        var part = ReadFixture(src);
        var d1 = part.Textures.Single(t => t.Kind == "Diffuse" && t.Tile == 1001);
        var d2 = part.Textures.Single(t => t.Kind == "Diffuse" && t.Tile == 1002);
        Assert.NotEqual(File.ReadAllBytes(Path.Combine(src, d1.RelativePath)), File.ReadAllBytes(Path.Combine(src, d2.RelativePath)));
        var moved = new Dictionary<string, string>();
        foreach (var (d, sub) in new[] { (d1, "a"), (d2, "b") })
        {
            string rel = Path.Combine(sub, "Same.png");
            Directory.CreateDirectory(Path.Combine(src, sub));
            File.Move(Path.Combine(src, d.RelativePath), Path.Combine(src, rel));
            var clone = new AgrTextureInfo
            {
                File = "Same.png", RelativePath = rel, Stem = d.Stem, Kind = d.Kind, KindRaw = d.KindRaw, Set = d.Set, Tile = d.Tile,
            };
            foreach (var pi in typeof(AgrTextureInfo).GetProperties().Where(x => x.CanWrite && x.Name is not ("File" or "RelativePath")))
            {
                pi.SetValue(clone, pi.GetValue(d));
            }
            part.Textures[part.Textures.IndexOf(d)] = clone;
            moved[d.RelativePath] = rel;
        }
        foreach (var ti in part.Tiles)
        {
            if (ti.MapPaths.TryGetValue("Diffuse", out var old) && moved.TryGetValue(old, out var rel))
            {
                ti.MapPaths["Diffuse"] = rel;
                ti.MapFiles["Diffuse"] = "Same.png";
            }
        }
        var options = new AgrGltfWriteOptions { SameVolume = (_, _) => sameVolume };
        string outDir = Path.Combine(tmp.Path, "o");
        var res = new AgrGltfWriter(part, src, options).Write(outDir);
        var same = res.Images.Where(i => i.File == "Same.png").ToList();
        Assert.Equal(2, same.Count);
        Assert.Equal(2, same.Select(i => i.Uri).Distinct().Count());
        Assert.Equal(sameVolume ? new[] { "../s/a/Same.png", "../s/b/Same.png" } : new[] { "a/Same.png", "b/Same.png" },
                     same.Select(i => i.Uri).OrderBy(u => u, StringComparer.Ordinal).ToArray());
        foreach (var i in same)
        {
            Assert.Equal(!sameVolume, i.Copied);
            Assert.Equal(File.ReadAllBytes(i.Source), File.ReadAllBytes(Path.GetFullPath(Path.Combine(outDir, Uri.UnescapeDataString(i.Uri)))));
        }
        var uris = Json(res.GltfPath)["images"]!.AsArray().Select(i => (string)i!["uri"]!).ToList();
        Assert.Equal(res.Images.Count, uris.Distinct().Count());
    }

    [Fact]
    public void OtherVolume_ImagesStreamCopiedNextToGltf_SameVolumeLinksOnly()
    {
        var part = ReadFixture();
        using var tmp = new TempDir("c1b-vol");
        var asked = new List<string>();
        var options = new AgrGltfWriteOptions
        {
            SameVolume = (source, output) =>
            {
                lock (asked) asked.Add(source);
                return false;
            },
        };
        string outDir = Path.Combine(tmp.Path, "другой том #2");
        var res = new AgrGltfWriter(part, TestPaths.FixtureDir, options).Write(outDir);
        Assert.Equal(5, asked.Count);
        Assert.Equal(5, res.Images.Count);
        var uris = Json(res.GltfPath)["images"]!.AsArray().Select(i => (string)i!["uri"]!).ToList();
        foreach (var img in res.Images)
        {
            Assert.True(img.Copied);
            Assert.Equal(Uri.EscapeDataString(img.File), img.Uri);
            Assert.Contains(img.Uri, uris);
            Assert.Equal(File.ReadAllBytes(img.Source), File.ReadAllBytes(Path.Combine(outDir, img.File)));
            Assert.Equal(new FileInfo(img.Source).Length, img.Bytes);
        }
        Assert.Equal(5, model(res).LogicalImages.Count);

        // Тот же том: исходник рядом (короткий путь), чтобы проверялась именно ссылка, а не предел длины пути.
        string src = Path.Combine(tmp.Path, "s");
        TestPaths.CopyDirectory(TestPaths.FixtureDir, src);
        string same = Path.Combine(tmp.Path, "same");
        var res2 = new AgrGltfWriter(ReadFixture(src), src).Write(same);
        Assert.Equal(5, res2.Images.Count);
        Assert.All(res2.Images, i => Assert.False(i.Copied));
        Assert.All(res2.Images, i => Assert.StartsWith("../s/", i.Uri));
        Assert.Empty(Directory.GetFiles(same, "*.png"));

        Assert.True(AgrGltfWriter.SameVolumeByRoot(@"G:\a\b.png", @"g:\c"));
        Assert.False(AgrGltfWriter.SameVolumeByRoot(@"G:\a\b.png", @"C:\c"));
        Assert.False(AgrGltfWriter.SameVolumeByRoot(@"\\srv\share\b.png", @"G:\c"));

        static ModelRoot model(AgrGltfResult r) => LoadStrict(r.GltfPath);
    }

    [Fact]
    public void EightParallelWriters_SharedPart_IdenticalFiles()
    {
        var part = ReadFixture();
        using var tmp = new TempDir("c1b-par");
        var results = new AgrGltfResult[8];
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 },
                     i => results[i] = new AgrGltfWriter(part, TestPaths.FixtureDir).Write(Path.Combine(tmp.Path, "o" + i)));
        string gltf0 = File.ReadAllText(results[0].GltfPath);
        byte[] bin0 = File.ReadAllBytes(results[0].BinPath);
        for (int i = 1; i < 8; i++)
        {
            Assert.Equal(gltf0, File.ReadAllText(results[i].GltfPath));
            Assert.Equal(bin0, File.ReadAllBytes(results[i].BinPath));
        }
    }

    [Fact]
    public void TexturesWithoutStatistics_Throw()
    {
        var part = ReadFixture(analyze: false);
        using var tmp = new TempDir("c1b-noan");
        var ex = Assert.Throws<InvalidOperationException>(() => new AgrGltfWriter(part, TestPaths.FixtureDir).Write(tmp.Path));
        Assert.Contains("AnalyzeTextures", ex.Message, StringComparison.Ordinal);
    }
}
