using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit.Abstractions;

namespace Connector.AgrConversion.Tests;

/// <summary>Статистика текстур WIC: порог заглушки 2/255, 16-битные PNG (C1 (з)), R-канал ERM (C1 (д)), альфа.</summary>
public class TextureAnalyzerTests
{
    readonly ITestOutputHelper _out;

    public TextureAnalyzerTests(ITestOutputHelper output) => _out = output;

    static AgrTextureInfo Analyze(string dir, string name, int w, int h, int channels, int bits, Func<int, int, int[]> pixel)
    {
        string path = Path.Combine(dir, name);
        PngTestWriter.Write(path, w, h, channels, bits, pixel);
        return new AgrTextureAnalyzer().Analyze(path, name);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(10, false)]
    public void Uniform_IsSpanUpTo2Of255(int span, bool uniform)
    {
        using var tmp = new TempDir("span");
        var info = Analyze(tmp.Path, "T_X_Diffuse_1.1001.png", 4, 2, 3, 8, (x, y) => new[] { 50, x == 0 ? 100 + span : 100, 70 });
        Assert.Equal(uniform, info.Uniform);
        Assert.Equal(span, info.Span255, 6);
        Assert.Equal(8, info.BitDepth);
    }

    [Fact]
    public void Png16_IsReadNatively_AsValueOver65535()
    {
        using var tmp = new TempDir("png16");
        var rgb = Analyze(tmp.Path, "T_X_Diffuse_1.1001.png", 4, 4, 3, 16, (x, y) => new[] { 32768, 16384, 65535 });
        Assert.Equal(16, rgb.BitDepth);
        Assert.Equal(2, rgb.ColorType);
        Assert.Equal("Rgb48", rgb.PixelFormat);
        Assert.Equal(32768 / 65535.0, rgb.Mean[0], 5);
        Assert.Equal(16384 / 65535.0, rgb.Mean[1], 5);
        Assert.Equal(1.0, rgb.Mean[2], 5);
        Assert.True(rgb.Uniform);
        Assert.Equal(new[] { 128, 64, 255 }, rgb.Rgb255);
        Assert.False(rgb.HasAlpha);

        var rgba = Analyze(tmp.Path, "T_X_Diffuse_1.1002.png", 4, 4, 4, 16, (x, y) => new[] { 1000, 2000, 3000, y < 2 ? 0 : 65535 });
        Assert.Equal("Rgba64", rgba.PixelFormat);
        Assert.True(rgba.HasAlpha);
        Assert.Equal("MASK", rgba.Alpha!.Mode);
        Assert.Equal(0.5, rgba.Alpha.Mean, 5);
        Assert.Equal(0.5, rgba.Alpha.FracBelowHalf, 5);

        // разброс 3/255 в 16-битных единицах — не заглушка
        var span = Analyze(tmp.Path, "T_X_Diffuse_1.1003.png", 4, 1, 3, 16, (x, y) => new[] { x == 0 ? 30000 + 3 * 257 : 30000, 30000, 30000 });
        Assert.False(span.Uniform);
        Assert.Equal(3.0, span.Span255, 3);

        var gray = Analyze(tmp.Path, "T_X_Normal_1.1004.png", 2, 2, 1, 16, (x, y) => new[] { 65535 });
        Assert.Equal("Gray16", gray.PixelFormat);
        Assert.Equal(new[] { 255, 255, 255 }, gray.Rgb255);

        // для отчёта: что дал бы перевод WIC 48 бит → Bgra32 (причина читать 16 бит в родном формате)
        using var fs = File.OpenRead(Path.Combine(tmp.Path, "T_X_Diffuse_1.1001.png"));
        var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                                         BitmapCacheOption.OnLoad).Frames[0];
        var conv = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var px = new byte[4 * 4 * 4];
        conv.CopyPixels(px, 16, 0);
        _out.WriteLine($"WIC Rgb48 (32768, 16384, 65535) → Bgra32 RGB = ({px[2]}, {px[1]}, {px[0]})");
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "agr-c1a-tests", "wic16.txt"),
                           $"{DateTime.Now:s} WIC Rgb48 (32768, 16384, 65535) -> Bgra32 RGB = ({px[2]}, {px[1]}, {px[0]}){Environment.NewLine}");
    }

    [Fact]
    public void ErmRed_EmissiveIsDetected()
    {
        using var tmp = new TempDir("erm");
        // 1 пиксель из 100 с R=255: доля 0,01 > 0,001 — emissive есть
        var spot = Analyze(tmp.Path, "T_X_ERM_1.1001.png", 10, 10, 3, 8, (x, y) => new[] { x == 0 && y == 0 ? 255 : 0, x * 20, 0 });
        Assert.True(spot.ErmRed!.CarriesEmissive);
        Assert.Equal(0.01, spot.ErmRed.FracAbove005, 6);
        Assert.Equal(255, spot.ErmRed.Max255);

        // R=12 (< 0,05·255) везде — нет
        var low = Analyze(tmp.Path, "T_X_ERM_1.1002.png", 10, 10, 3, 8, (x, y) => new[] { 12, x * 20, 0 });
        Assert.False(low.ErmRed!.CarriesEmissive);
        Assert.Equal(0.0, low.ErmRed.FracAbove005);

        // заглушки: R=13 — есть, R=12 — нет
        Assert.True(Analyze(tmp.Path, "T_X_ERM_1.1003.png", 2, 2, 3, 8, (x, y) => new[] { 13, 128, 0 }).ErmRed!.CarriesEmissive);
        Assert.False(Analyze(tmp.Path, "T_X_ERM_1.1004.png", 2, 2, 3, 8, (x, y) => new[] { 12, 128, 0 }).ErmRed!.CarriesEmissive);

        // у не-ERM статистики R нет
        Assert.Null(Analyze(tmp.Path, "T_X_Diffuse_1.1005.png", 2, 2, 3, 8, (x, y) => new[] { 255, 0, 0 }).ErmRed);
    }

    [Theory]
    [InlineData("T_ProektiruemyjProezd_001_Diffuse_1.1001.png", "ProektiruemyjProezd_001", "Diffuse", "1", 1001)]
    [InlineData("T_Some_Part_ERM_12.1011.png", "Some_Part", "ERM", "12", 1011)]
    [InlineData("T_A_BaseColor_2.1002.PNG", "A", "Diffuse", "2", 1002)]
    public void TextureName_IsParsed(string file, string stem, string kind, string set, int tile)
    {
        var n = AgrTextureAnalyzer.ParseName(file);
        Assert.NotNull(n);
        Assert.Equal((stem, kind, set, tile), (n!.Stem, n.Kind, n.Set, n.Tile));
        Assert.Null(AgrTextureAnalyzer.ParseName("preview.png"));
    }
}

/// <summary>Паспорт geojson: точка МСК-77 [x — восток, y — север], imageBase64 вне свойств, Glasses.</summary>
public class GeoJsonTests
{
    static string WriteGeo(string dir, object geometry, bool withImage = true)
    {
        var png = Path.Combine(dir, "p.png");
        PngTestWriter.Write(png, 5, 3, 3, 8, (x, y) => new[] { 1, 2, 3 });
        var props = new Dictionary<string, object> { ["h_relief"] = 5.5, ["name"] = "SM_X_001" };
        if (withImage) props["imageBase64"] = Convert.ToBase64String(File.ReadAllBytes(png));
        var doc = new Dictionary<string, object>
        {
            ["type"] = "FeatureCollection",
            ["features"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["type"] = "Feature",
                    ["properties"] = props,
                    ["geometry"] = geometry,
                    ["Glasses"] = new object[] { new Dictionary<string, object> { ["M_X_Glass_1"] = new { transparency = 1 } } },
                },
            },
        };
        string path = Path.Combine(dir, "SM_X_001.geojson");
        File.WriteAllText(path, JsonSerializer.Serialize(doc));
        return path;
    }

    [Fact]
    public void Point_FirstIsEast_SecondIsNorth()
    {
        using var tmp = new TempDir("geo");
        var pass = AgrGeoJsonReader.Read("SM_X_001", new[] { WriteGeo(tmp.Path, new { type = "Point", coordinates = new[] { 100.5, 200.25, 7.0 } }) });
        Assert.Equal(new[] { 100.5, 200.25, 7.0 }, pass.Point);
        Assert.Equal(5.5, AgrGeoJsonReader.HRelief(pass));
        var f = Assert.Single(pass.Features);
        Assert.False(f.Properties.ContainsKey("imageBase64"));
        Assert.Equal(new[] { 5, 3 }, f.Preview!.Size);
        Assert.Equal("png", f.Preview.Format);
        Assert.Equal(new[] { "M_X_Glass_1" }, pass.Glasses.Keys);
    }

    [Fact]
    public void NoPoint_GivesNoPlacement()
    {
        using var tmp = new TempDir("geo2");
        var poly = new { type = "Polygon", coordinates = new[] { new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 }, new[] { 0.0, 1.0 }, new[] { 0.0, 0.0 } } } };
        var pass = AgrGeoJsonReader.Read("SM_X_001", new[] { WriteGeo(tmp.Path, poly, withImage: false) });
        Assert.Null(pass.Point);
        Assert.Null(Assert.Single(pass.Features).Preview);
    }
}
