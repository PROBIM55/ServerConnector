using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Connector.AgrConversion.Tests;

/// <summary>
/// Сверка с эталоном Blender (B1) по 16 частям пакета АВТОМАГИСТРАЛЬ — только при заданной AGR_REF_ROOT
/// (G:\00_Projects\Structura_Main\tmp\agr_poc), иначе пропуск. Допуски — как в S2 Compare.cs: треугольники и тайлы
/// точно (имена материалов без суффикса Blender .NNN), габарит до 1 см, треугольники UCX равны; плюс заглушки
/// (флаг однотонности, rgb255, размер) и классы/карты тайлов.
/// Вершины — против glTF из tiles_src (выгрузка Blender, Y вверх): для каждого тайла множества позиций углов граней
/// совпадают в пределах 1 мм в обе стороны; контроль — зеркальная ось Y должна не совпасть.
/// </summary>
public class ReferenceTests
{
    public static readonly string[] Parts =
        Enumerable.Range(1, 15).Select(i => $"SM_ProektiruemyjProezd_{i:000}").Append("SM_ProektiruemyjProezd_Ground").ToArray();

    public static IEnumerable<object[]> PartData => Parts.Select(p => new object[] { p });

    static readonly Regex BlenderSuffix = new(@"\.\d{3}$", RegexOptions.CultureInvariant);

    const double BboxTolM = 0.01;
    const double PositionTolM = 0.001;

    readonly ITestOutputHelper _out;

    public ReferenceTests(ITestOutputHelper output) => _out = output;

    static string Norm(string material) => BlenderSuffix.Replace(material, "");

    static string C1aDir(string sub)
    {
        string dir = Path.Combine(TestPaths.RefRoot!, "tmp", "c1a", sub);
        Directory.CreateDirectory(dir);
        return dir;
    }

    void Log(string line)
    {
        _out.WriteLine(line);
        lock (Parts)
        {
            File.AppendAllText(Path.Combine(C1aDir("logs"), "ref_parts.log"),
                               $"{DateTime.Now:s} {line}{Environment.NewLine}");
        }
    }

    [RefTheory]
    [MemberData(nameof(PartData))]
    public void Part_MatchesBlenderReference(string partName)
    {
        string root = TestPaths.RefRoot!;
        var sw = Stopwatch.StartNew();
        var part = new AgrPartReader().Read(Path.Combine(root, "batch", "src", partName));
        double tRead = sw.Elapsed.TotalSeconds;
        AgrReport.Write(part, Path.Combine(C1aDir("reports"), partName + ".json"));

        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "batch", "ref", partName + ".json")));
        var r = doc.RootElement;
        var errors = new List<string>();

        var tri = r.GetProperty("triangles");
        int trisRef = tri.GetProperty("model").GetInt32();
        if (part.ModelTriangles != trisRef) errors.Add($"треугольников {part.ModelTriangles} / эталон {trisRef}");
        part.Dropped.Collision.TryGetValue("UCX_", out var ucx);
        int ucxTris = ucx?.Triangles ?? 0, ucxObj = ucx?.Objects ?? 0;
        if (ucxTris != tri.GetProperty("ucx").GetInt32()) errors.Add($"UCX тр. {ucxTris} / эталон {tri.GetProperty("ucx").GetInt32()}");
        if (ucxObj != tri.GetProperty("ucx_objects").GetInt32()) errors.Add($"UCX объектов {ucxObj} / эталон {tri.GetProperty("ucx_objects").GetInt32()}");

        var rb = r.GetProperty("bbox_model_m");
        double dev = 0;
        for (int k = 0; k < 3; k++)
        {
            dev = Math.Max(dev, Math.Abs(part.Bounds.Min[k] - rb.GetProperty("min")[k].GetDouble()));
            dev = Math.Max(dev, Math.Abs(part.Bounds.Max[k] - rb.GetProperty("max")[k].GetDouble()));
        }
        if (dev > BboxTolM) errors.Add($"габарит отличается на {dev * 1000:0.##} мм");

        // тайлы: грани, класс, карты
        var mine = part.Tiles.GroupBy(t => Norm(t.Material) + "|" + t.Tile).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var refTiles = r.GetProperty("tiles").EnumerateArray()
            .GroupBy(t => Norm(t.GetProperty("material").GetString()!) + "|" + t.GetProperty("tile").GetInt32())
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        int tilesEqual = 0, classMis = 0, mapsMis = 0;
        foreach (var key in mine.Keys.Union(refTiles.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            mine.TryGetValue(key, out var a);
            refTiles.TryGetValue(key, out var b);
            int fa = a?.Sum(t => t.Faces) ?? 0, fb = b?.Sum(t => t.GetProperty("faces").GetInt32()) ?? 0;
            if (fa != fb)
            {
                errors.Add($"тайл {key}: граней {fa} / эталон {fb}");
                continue;
            }
            tilesEqual++;
            if (a!.Count != 1 || b!.Count != 1) continue;
            var ta = a[0];
            var tb = b[0];
            string cb = tb.GetProperty("class").GetString()!;
            if (ta.Class != cb)
            {
                classMis++;
                errors.Add($"тайл {key}: класс {ta.Class} / эталон {cb}");
            }
            foreach (var m in tb.GetProperty("maps").EnumerateObject())
            {
                ta.Maps.TryGetValue(m.Name, out var va);
                if (va != m.Value.GetString())
                {
                    mapsMis++;
                    errors.Add($"тайл {key} {m.Name}: {va ?? "нет"} / эталон {m.Value.GetString()}");
                }
            }
        }

        // текстуры: флаг однотонности, rgb255 заглушек, размер
        var tex = part.Textures.ToDictionary(t => t.File, StringComparer.OrdinalIgnoreCase);
        int flagMis = 0, rgbMis = 0, sizeMis = 0, stubs = 0;
        foreach (var t in r.GetProperty("textures").EnumerateArray())
        {
            string f = t.GetProperty("file").GetString()!;
            if (!tex.TryGetValue(f, out var a))
            {
                errors.Add($"{f}: нет в разборе");
                continue;
            }
            bool ur = t.GetProperty("uniform").GetBoolean();
            if (a.Uniform != ur)
            {
                flagMis++;
                errors.Add($"{f}: однотонность {a.Uniform} / эталон {ur}");
            }
            if (a.W != t.GetProperty("w").GetInt32() || a.H != t.GetProperty("h").GetInt32())
            {
                sizeMis++;
                errors.Add($"{f}: размер {a.W}x{a.H}");
            }
            if (a.Uniform && ur)
            {
                stubs++;
                var rr = t.GetProperty("rgb255").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (!a.Rgb255.SequenceEqual(rr))
                {
                    rgbMis++;
                    errors.Add($"{f}: rgb [{string.Join(",", a.Rgb255)}] / эталон [{string.Join(",", rr)}]");
                }
            }
        }
        if (tex.Count != r.GetProperty("textures").GetArrayLength()) errors.Add($"текстур {tex.Count} / эталон {r.GetProperty("textures").GetArrayLength()}");

        Log(string.Create(CultureInfo.InvariantCulture,
            $"{partName}: tris {part.ModelTriangles}/{trisRef} ucx {ucxTris}/{tri.GetProperty("ucx").GetInt32()} bbox {dev * 1000:0.##} mm " +
            $"tiles {tilesEqual}/{refTiles.Count} classMis {classMis} mapsMis {mapsMis} tex {tex.Count} stubs {stubs} " +
            $"flagMis {flagMis} rgbMis {rgbMis} sizeMis {sizeMis} warnings {part.Warnings.Count} t_read {tRead:0.0} s " +
            $"(import {part.TimingsS["import"]:0.0} s, textures {part.TimingsS["textures"]:0.0} s) ok={errors.Count == 0}"));
        Assert.True(errors.Count == 0, partName + ":\n" + string.Join("\n", errors.Take(40)));
    }

    [RefTheory]
    [MemberData(nameof(PartData))]
    public void Positions_MatchTilesSrcGltf_AndMirroredAxisDoesNot(string partName)
    {
        string root = TestPaths.RefRoot!;
        var part = new AgrPartReader(new AgrReadOptions { AnalyzeTextures = false }).Read(Path.Combine(root, "batch", "src", partName));
        var gltf = ReadGltfPositions(Path.Combine(root, "tiles_src", partName, partName + ".gltf"));

        // позиции углов граней по тайлам
        var mine = new Dictionary<int, List<float[]>>();
        foreach (var m in part.Meshes)
        {
            var seen = new Dictionary<int, HashSet<int>>();
            for (int t = 0; t < m.TriangleCount; t++)
            {
                int tile = m.FaceTiles[t];
                if (!seen.TryGetValue(tile, out var set)) seen[tile] = set = new HashSet<int>();
                for (int c = 0; c < 3; c++) set.Add(m.Indices[t * 3 + c]);
            }
            foreach (var (tile, set) in seen)
            {
                if (!mine.TryGetValue(tile, out var list)) mine[tile] = list = new List<float[]>();
                foreach (int v in set) list.Add(new[] { m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2] });
            }
        }

        var errors = new List<string>();
        if (!mine.Keys.OrderBy(k => k).SequenceEqual(gltf.Keys.OrderBy(k => k)))
        {
            errors.Add($"тайлы {string.Join(",", mine.Keys.OrderBy(k => k))} / glTF {string.Join(",", gltf.Keys.OrderBy(k => k))}");
        }
        long missG = 0, missM = 0, total = 0, mirrorHit = 0, mirrorTotal = 0;
        double maxDev = 0;
        foreach (var tile in mine.Keys.Intersect(gltf.Keys))
        {
            var gridMine = new PointGrid(mine[tile], PositionTolM);
            var gridGltf = new PointGrid(gltf[tile], PositionTolM);
            foreach (var p in gltf[tile])
            {
                double d = gridMine.Nearest(p);
                total++;
                if (d > PositionTolM) missG++;
                else maxDev = Math.Max(maxDev, d);
            }
            foreach (var p in mine[tile])
            {
                if (gridGltf.Nearest(p) > PositionTolM) missM++;
                mirrorTotal++;
                if (gridGltf.Nearest(new[] { p[0], -p[1], p[2] }) <= PositionTolM) mirrorHit++;
            }
        }
        double mirrorFrac = mirrorTotal == 0 ? 1 : mirrorHit / (double)mirrorTotal;
        if (missG > 0) errors.Add($"вершин glTF без пары: {missG} из {total}");
        if (missM > 0) errors.Add($"вершин разбора без пары в glTF: {missM}");
        if (mirrorFrac >= 0.5) errors.Add($"зеркальная ось Y совпала у {mirrorFrac:P1} вершин — сверка нечувствительна");
        Log(string.Create(CultureInfo.InvariantCulture,
            $"{partName} positions: gltf {total} miss {missG}, mine miss {missM}, max dev {maxDev * 1000:0.###} mm, " +
            $"mirror-Y match {mirrorFrac:P2} ok={errors.Count == 0}"));
        Assert.True(errors.Count == 0, partName + ":\n" + string.Join("\n", errors));
    }

    /// <summary>Позиции примитивов glTF по extras.agr.udim_tile, из Y-up в оси АГР: x = xg, y = -zg, z = yg.</summary>
    static Dictionary<int, List<float[]>> ReadGltfPositions(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var g = doc.RootElement;
        foreach (var n in g.GetProperty("nodes").EnumerateArray())
        {
            if (n.TryGetProperty("matrix", out _) || n.TryGetProperty("translation", out _) || n.TryGetProperty("rotation", out _)
                || n.TryGetProperty("scale", out _))
            {
                throw new InvalidDataException($"{path}: у узла glTF есть TRS — сверка позиций его не учитывает");
            }
        }
        byte[] bin = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!,
            Uri.UnescapeDataString(g.GetProperty("buffers")[0].GetProperty("uri").GetString()!)));
        var materials = g.GetProperty("materials");
        var accessors = g.GetProperty("accessors");
        var views = g.GetProperty("bufferViews");
        var result = new Dictionary<int, List<float[]>>();
        foreach (var mesh in g.GetProperty("meshes").EnumerateArray())
        {
            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                if (!prim.TryGetProperty("material", out var mi)) continue;
                var mat = materials[mi.GetInt32()];
                if (!mat.TryGetProperty("extras", out var ex) || !ex.TryGetProperty("agr", out var agr)
                    || !agr.TryGetProperty("udim_tile", out var ut)) continue;
                int tile = ut.GetInt32();
                var acc = accessors[prim.GetProperty("attributes").GetProperty("POSITION").GetInt32()];
                if (acc.GetProperty("componentType").GetInt32() != 5126) throw new InvalidDataException("POSITION не float32");
                var view = views[acc.GetProperty("bufferView").GetInt32()];
                int offset = (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
                             + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
                int stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 12;
                int count = acc.GetProperty("count").GetInt32();
                if (!result.TryGetValue(tile, out var list)) result[tile] = list = new List<float[]>();
                for (int i = 0; i < count; i++)
                {
                    int o = offset + i * stride;
                    float xg = BitConverter.ToSingle(bin, o), yg = BitConverter.ToSingle(bin, o + 4), zg = BitConverter.ToSingle(bin, o + 8);
                    list.Add(new[] { xg, -zg, yg });
                }
            }
        }
        return result;
    }

    /// <summary>Решётка с ячейкой = допуску: ближайшая точка ищется в 27 соседних ячейках.</summary>
    sealed class PointGrid
    {
        readonly double _cell;
        readonly Dictionary<(long, long, long), List<float[]>> _cells = new();

        public PointGrid(IEnumerable<float[]> points, double cell)
        {
            _cell = cell;
            foreach (var p in points)
            {
                var k = Key(p);
                if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<float[]>();
                l.Add(p);
            }
        }

        (long, long, long) Key(float[] p) =>
            ((long)Math.Floor(p[0] / _cell), (long)Math.Floor(p[1] / _cell), (long)Math.Floor(p[2] / _cell));

        public double Nearest(float[] p)
        {
            var (kx, ky, kz) = Key(p);
            double best = double.PositiveInfinity;
            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            for (long dz = -1; dz <= 1; dz++)
            {
                if (!_cells.TryGetValue((kx + dx, ky + dy, kz + dz), out var l)) continue;
                foreach (var q in l)
                {
                    double ddx = p[0] - q[0], ddy = p[1] - q[1], ddz = p[2] - q[2];
                    double d = Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                    if (d < best) best = d;
                }
            }
            return best;
        }
    }
}
