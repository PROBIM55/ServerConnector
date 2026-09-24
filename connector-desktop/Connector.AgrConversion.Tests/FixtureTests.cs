using System.Text.Json.Nodes;

namespace Connector.AgrConversion.Tests;

/// <summary>
/// Малая фикстура SM_TestPart_001 (Fixtures/make_fixture.py, Blender 5.2): 7 треугольников модели в тайлах 1001/1002/1011,
/// стекло по паспорту, UCX-куб 12 тр., свет, заглушки/альфа/emissive/16 бит. Ожидаемые значения — из скрипта фикстуры.
/// </summary>
public class FixtureTests
{
    // вершины модели в осях АГР (мировые координаты Blender при создании фикстуры), метры
    static readonly double[][] ExpectedPositions =
    {
        new[] { 1.0, 2.0, 0.5 }, new[] { 2.0, 2.0, 0.5 }, new[] { 2.0, 3.0, 0.5 }, new[] { 1.0, 3.0, 0.5 },
        new[] { 3.0, 4.0, 1.0 }, new[] { 4.0, 4.0, 1.0 }, new[] { 4.0, 5.0, 1.5 }, new[] { 3.0, 5.0, 1.5 },
        new[] { 1.0, 6.0, 0.8 }, new[] { 2.0, 6.0, 0.8 }, new[] { 2.0, 7.0, 0.8 }, new[] { 1.0, 7.0, 0.8 },
        new[] { 3.0, 6.0, 1.2 }, new[] { 4.0, 6.0, 1.2 }, new[] { 4.0, 7.0, 1.2 },
    };

    const string Main = "M_TestPart_001_Main_1";
    const string Glass = "M_TestPart_001_MainGlass_1";

    static readonly Lazy<AgrPart> Part = new(() => new AgrPartReader().Read(TestPaths.FixtureDir));

    static double Dist(IReadOnlyList<double> a, IReadOnlyList<double> b) =>
        Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

    [Fact]
    public void Geometry_IsInAgrAxes_AndBboxExcludesCollision()
    {
        var part = Part.Value;
        Assert.Equal("SM_TestPart_001", part.Name);
        Assert.Equal(7, part.ModelTriangles);
        Assert.DoesNotContain(part.Warnings, w => w.StartsWith("оси/единицы", StringComparison.Ordinal));
        for (int k = 0; k < 3; k++)
        {
            Assert.Equal(new[] { 1.0, 2.0, 0.5 }[k], part.Bounds.Min[k], 4);
            Assert.Equal(new[] { 4.0, 7.0, 1.5 }[k], part.Bounds.Max[k], 4);
        }
        var actual = part.Meshes
            .SelectMany(m => Enumerable.Range(0, m.VertexCount)
                .Select(i => new double[] { m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2] }))
            .ToList();
        foreach (var e in ExpectedPositions)
        {
            Assert.True(actual.Any(a => Dist(a, e) < 1e-4), $"нет вершины ({string.Join("; ", e)})");
        }
        foreach (var a in actual)
        {
            Assert.True(ExpectedPositions.Any(e => Dist(a, e) < 1e-4), $"лишняя вершина ({string.Join("; ", a)})");
        }
    }

    [Fact]
    public void Normals_FollowTheSameAxes()
    {
        // грань тайла 1002 наклонена: нормаль (0; -0,4472; 0,8944) в осях АГР
        var part = Part.Value;
        var mesh = part.Meshes.Single(m => m.Material == Main);
        Assert.NotNull(mesh.Normals);
        int checkedCount = 0;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            double x = mesh.Positions[i * 3], y = mesh.Positions[i * 3 + 1];
            if (x > 2.9 && y > 3.9 && y < 5.1)
            {
                Assert.Equal(0.0, mesh.Normals![i * 3], 3);
                Assert.Equal(-0.4472, mesh.Normals[i * 3 + 1], 3);
                Assert.Equal(0.8944, mesh.Normals[i * 3 + 2], 3);
                checkedCount++;
            }
        }
        Assert.True(checkedCount >= 4);
    }

    [Fact]
    public void Collision_Light_AndUnusedAreReportedAsDropped()
    {
        var part = Part.Value;
        var ucx = Assert.Single(part.Dropped.Collision);
        Assert.Equal("UCX_", ucx.Key);
        Assert.Equal(1, ucx.Value.Objects);
        Assert.Equal(12, ucx.Value.Triangles);
        Assert.Equal(new[] { "SM_TestPart_001_Light.fbx" }, part.Dropped.LightFbx);
        Assert.Equal(new[] { "T_TestPart_001_Diffuse_1.1005.png" }, part.Dropped.UnusedTextures);
        Assert.Empty(part.Dropped.IgnoredFiles);
        Assert.Equal(0, part.StraddleFaces);
    }

    [Fact]
    public void Tiles_AreByUvCentroid_WithClassesAndMaps()
    {
        var part = Part.Value;
        var tiles = part.Tiles.ToDictionary(t => $"{t.Material}|{t.Tile}");
        Assert.Equal(
            new[] { $"{Glass}|1001", $"{Main}|1001", $"{Main}|1002", $"{Main}|1011" },
            tiles.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(2, tiles[$"{Main}|1001"].Faces);
        Assert.Equal(2, tiles[$"{Main}|1002"].Faces);
        Assert.Equal(2, tiles[$"{Main}|1011"].Faces);
        Assert.Equal(1, tiles[$"{Glass}|1001"].Faces);

        var t1001 = tiles[$"{Main}|1001"];
        Assert.Equal("real", t1001.Class);
        Assert.Equal("real 32x32", t1001.Maps["Diffuse"]);
        Assert.Equal("real 32x32", t1001.Maps["ERM"]);
        Assert.Equal("real 32x32", t1001.Maps["Normal"]);

        var t1002 = tiles[$"{Main}|1002"];
        Assert.Equal("real", t1002.Class);
        Assert.Equal("real 16x16", t1002.Maps["Diffuse"]);
        Assert.Equal("stub rgb[255, 101, 0]", t1002.Maps["ERM"]);
        Assert.Equal("stub rgb[128, 128, 255]", t1002.Maps["Normal"]);

        var t1011 = tiles[$"{Main}|1011"];
        Assert.Equal("stub", t1011.Class);
        Assert.Equal("stub rgb[200, 50, 25]", t1011.Maps["Diffuse"]);
        Assert.Equal("stub rgb[0, 128, 0]", t1011.Maps["ERM"]);
        Assert.Equal("stub rgb[128, 128, 255]", t1011.Maps["Normal"]);

        var glass = tiles[$"{Glass}|1001"];
        Assert.Equal("glass", glass.Class);
        Assert.Empty(glass.Maps);
    }

    [Fact]
    public void Textures_StubThreshold_Alpha_Emissive_16Bit()
    {
        var t = Part.Value.Textures.ToDictionary(x => x.File);
        var d1002 = t["T_TestPart_001_Diffuse_1.1002.png"];
        Assert.False(d1002.Uniform);
        Assert.Equal(10.0, d1002.Span255, 3);
        Assert.Equal("image", d1002.Role);

        var e1002 = t["T_TestPart_001_ERM_1.1002.png"];
        Assert.True(e1002.Uniform);
        Assert.Equal(2.0, e1002.Span255, 3);
        Assert.Equal(new[] { 255, 101, 0 }, e1002.Rgb255);
        Assert.Equal("const", e1002.Role);

        var alpha = t["T_TestPart_001_Diffuse_1.1011.png"].Alpha;
        Assert.NotNull(alpha);
        Assert.True(alpha!.NonTrivial);
        Assert.Equal("MASK", alpha.Mode);
        Assert.Equal(0.5, alpha.Mean, 4);
        Assert.Equal(0, alpha.Min255);
        Assert.Null(t["T_TestPart_001_Diffuse_1.1001.png"].Alpha);

        var n16 = t["T_TestPart_001_Normal_1.1011.png"];
        Assert.Equal(16, n16.BitDepth);
        Assert.True(n16.Uniform);
        Assert.Equal(new[] { 128, 128, 255 }, n16.Rgb255);

        Assert.Equal("unused", t["T_TestPart_001_Diffuse_1.1005.png"].Role);

        var emissive = Part.Value.Dropped.Emissive.Where(e => e.Red.CarriesEmissive).ToList();
        Assert.Equal("T_TestPart_001_ERM_1.1002.png", Assert.Single(emissive).File);
        Assert.False(t["T_TestPart_001_ERM_1.1001.png"].ErmRed!.CarriesEmissive);
        Assert.Equal(0.0, t["T_TestPart_001_ERM_1.1001.png"].ErmRed!.FracAbove005);
    }

    [Fact]
    public void Passport_PointIsEastNorth_WithGlassesAndPreview()
    {
        var part = Part.Value;
        Assert.NotNull(part.Placement);
        Assert.Equal(12345.678, part.Placement!.E, 6);
        Assert.Equal(23456.789, part.Placement.N, 6);
        Assert.Equal(145.25, part.Placement.DZ!.Value, 6);
        var pass = part.Passport!;
        Assert.Equal(new[] { Glass }, pass.Glasses.Keys);
        var f = Assert.Single(pass.Features);
        Assert.False(f.Properties.ContainsKey(AgrGeoJsonReader.ImageKey));
        Assert.Equal("SM_TestPart_001", (string?)f.Properties["name"]);
        Assert.NotNull(f.Preview);
        Assert.Equal("png", f.Preview!.Format);
        Assert.Equal(new[] { 3, 2 }, f.Preview.Size);
    }

    [Fact]
    public void Report_IsJson_WithTotalsTilesStubsAndWarnings()
    {
        var json = JsonNode.Parse(AgrReport.ToJson(Part.Value))!;
        Assert.Equal(7, (int)json["triangles"]!["model"]!);
        Assert.Equal(3, (int)json["triangles"]!["by_tile"]!["1001"]!);
        Assert.Equal(2, (int)json["triangles"]!["by_tile"]!["1002"]!);
        Assert.Equal(12, (int)json["triangles"]!["collision"]!["UCX_"]!["triangles"]!);
        Assert.Equal(4, json["tiles"]!.AsArray().Count);
        Assert.Equal(1, (int)json["tiles_summary"]!["stub_only"]!);
        Assert.Equal(1, (int)json["tiles_summary"]!["glass"]!);
        Assert.Equal(4.0, (double)json["bbox_model_m"]!["max"]![0]!, 4);
        Assert.Contains(json["stubs"]!.AsArray(), s => (string?)s!["file"] == "T_TestPart_001_ERM_1.1002.png");
        Assert.Contains(json["alpha"]!.AsArray(), s => (string?)s!["file"] == "T_TestPart_001_Diffuse_1.1011.png");
        Assert.Contains(json["warnings"]!.AsArray(), w => ((string?)w ?? "").Contains("ERM T_TestPart_001_ERM_1.1002.png"));
        Assert.Contains(json["warnings"]!.AsArray(), w => ((string?)w ?? "").Contains("битность 16"));
        Assert.Equal(12345.678, (double)json["placement"]!["e"]!, 6);
    }
}
