using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;
using Xunit.Abstractions;

namespace Connector.AgrConversion.Tests;

/// <summary>
/// Запись glTF против промежуточного glTF S1a-1 (tiles_src, выгрузка Blender) по 16 частям — только при заданной
/// AGR_REF_ROOT (G:\00_Projects\Structura_Main\tmp\agr_poc), иначе пропуск. Чтение C1a → запись в tmp/c1b (удаляется в
/// finally) → строгая проверка SharpGLTF (картинки — заглушкой, но их файлы обязаны найтись по URI) → сверка по ключу
/// (материал без суффикса Blender .NNN, UDIM-тайл): треугольники; позиции вершин в пределах 1 мм в обе стороны,
/// а зеркальная ось (z glTF = север АГР) совпадать не должна; у каждой вершины эталона — пара с той же UV (сдвиг в клетку
/// тайла, v перевёрнут) в пределах 1e-4; alphaMode, множители, картинки каналов; samplers; узлы.
/// Время и память — в c1b/logs/ref_parts.log.
/// </summary>
public class GltfReferenceTests
{
    static readonly Regex BlenderSuffix = new(@"\.\d{3}$", RegexOptions.CultureInvariant);
    const double PositionTolM = 0.001;
    const double FactorTol = 1e-5;
    const double UvTol = 1e-4;
    const string PlaceholderPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC";

    readonly ITestOutputHelper _out;

    public GltfReferenceTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> PartData => ReferenceTests.PartData;

    static string Norm(string material) => BlenderSuffix.Replace(material, "");

    void Log(string line)
    {
        _out.WriteLine(line);
        string dir = Path.Combine(TestPaths.RefRoot!, "c1b", "logs");
        Directory.CreateDirectory(dir);
        lock (BlenderSuffix)
        {
            File.AppendAllText(Path.Combine(dir, "ref_parts.log"), $"{DateTime.Now:s} {line}{Environment.NewLine}");
        }
    }

    [RefTheory]
    [MemberData(nameof(PartData))]
    public void Part_WritesLikeTilesSrc(string partName)
    {
        string root = TestPaths.RefRoot!;
        string src = Path.Combine(root, "batch", "src", partName);
        string refGltf = Path.Combine(root, "tiles_src", partName, partName + ".gltf");
        string work = Path.Combine(root, "tmp", "c1b", "ref-" + partName + "-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            GC.Collect();
            long alloc0 = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            var part = new AgrPartReader().Read(src);
            double readS = sw.Elapsed.TotalSeconds;
            var res = new AgrGltfWriter(part, src).Write(work);
            double writeS = sw.Elapsed.TotalSeconds - readS;
            long allocMb = (GC.GetTotalAllocatedBytes(true) - alloc0) >> 20;
            long peakMb;
            using (var proc = Process.GetCurrentProcess())
            {
                peakMb = proc.PeakWorkingSet64 >> 20;
            }

            ValidateStrict(res.GltfPath);
            var mine = GltfSummary.Load(res.GltfPath);
            var reference = GltfSummary.Load(refGltf);
            var errors = Compare(mine, reference, out string stats);
            Log(string.Create(CultureInfo.InvariantCulture,
                $"{partName} read {readS:0.00} s write {writeS:0.00} s alloc {allocMb} MB peak_ws_process {peakMb} MB | " +
                $"prims {res.Primitives} tris {res.Triangles} mats {res.Materials.Count} images {res.Images.Count} " +
                $"copied {res.Images.Count(i => i.Copied)} | {stats} ok={errors.Count == 0}"));
            Assert.True(errors.Count == 0, partName + ":\n" + string.Join("\n", errors.Take(40)));
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    /// <summary>SharpGLTF со строгой проверкой; вместо PNG — заглушка (без сотен МБ в памяти), но файл по URI обязан быть.</summary>
    static void ValidateStrict(string gltf)
    {
        string dir = Path.GetDirectoryName(gltf)!;
        byte[] placeholder = Convert.FromBase64String(PlaceholderPng);
        var missing = new List<string>();
        var ctx = ReadContext.Create(name =>
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path))
            {
                path = Path.Combine(dir, Uri.UnescapeDataString(name));
            }
            if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(path)) missing.Add(name);
                return new ArraySegment<byte>(placeholder);
            }
            return new ArraySegment<byte>(File.ReadAllBytes(path));
        });
        ctx.Validation = ValidationMode.Strict;
        var model = ctx.ReadSchema2(Path.GetFileName(gltf));
        Assert.True(missing.Count == 0, "нет картинок по URI: " + string.Join(", ", missing.Take(5)));
        Assert.NotEmpty(model.LogicalMeshes);
    }

    static List<string> Compare(GltfSummary a, GltfSummary r, out string stats)
    {
        var errors = new List<string>();
        var keys = a.Materials.Keys.Union(r.Materials.Keys).OrderBy(k => k.Mat, StringComparer.Ordinal).ThenBy(k => k.Tile).ToList();
        long missR = 0, missA = 0, totalR = 0, mirrorHit = 0, mirrorTotal = 0, uvMissR = 0;
        double maxDev = 0;
        foreach (var k in keys)
        {
            string key = k.Mat + "|" + k.Tile;
            if (!a.Materials.TryGetValue(k, out var ma)) { errors.Add($"{key}: нет в записи"); continue; }
            if (!r.Materials.TryGetValue(k, out var mr)) { errors.Add($"{key}: лишний материал"); continue; }
            long ta = a.Triangles.GetValueOrDefault(k), tr = r.Triangles.GetValueOrDefault(k);
            if (ta != tr) errors.Add($"{key}: треугольников {ta}, эталон {tr}");
            if (ma.AlphaMode != mr.AlphaMode) errors.Add($"{key}: alphaMode {ma.AlphaMode}, эталон {mr.AlphaMode}");
            if (ma.AlphaMode == "MASK" && Math.Abs(ma.Cutoff - mr.Cutoff) > 1e-6) errors.Add($"{key}: alphaCutoff {ma.Cutoff} / {mr.Cutoff}");
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(ma.BaseColor[i] - mr.BaseColor[i]) > FactorTol)
                    errors.Add($"{key}: baseColorFactor[{i}] {ma.BaseColor[i]} / {mr.BaseColor[i]}");
            }
            if (Math.Abs(ma.Metallic - mr.Metallic) > FactorTol) errors.Add($"{key}: metallic {ma.Metallic} / {mr.Metallic}");
            if (Math.Abs(ma.Roughness - mr.Roughness) > FactorTol) errors.Add($"{key}: roughness {ma.Roughness} / {mr.Roughness}");
            if (ma.DoubleSided != mr.DoubleSided) errors.Add($"{key}: doubleSided {ma.DoubleSided} / {mr.DoubleSided}");
            if (ma.Base != mr.Base) errors.Add($"{key}: baseColorTexture {ma.Base} / {mr.Base}");
            if (ma.Mr != mr.Mr) errors.Add($"{key}: metallicRoughnessTexture {ma.Mr} / {mr.Mr}");
            if (ma.Normal != mr.Normal) errors.Add($"{key}: normalTexture {ma.Normal} / {mr.Normal}");

            var pa = a.Positions.GetValueOrDefault(k) ?? new List<float[]>();
            var pr = r.Positions.GetValueOrDefault(k) ?? new List<float[]>();
            var gridA = new PointGrid(pa, PositionTolM);
            var gridR = new PointGrid(pr, PositionTolM);
            foreach (var p in pr)
            {
                totalR++;
                double d = gridA.Nearest(p);
                if (d > PositionTolM) missR++;
                else
                {
                    maxDev = Math.Max(maxDev, d);
                    if (gridA.Nearest(p, UvTol) > PositionTolM) uvMissR++;
                }
            }
            foreach (var p in pa)
            {
                if (gridR.Nearest(p) > PositionTolM) missA++;
                mirrorTotal++;
                if (gridR.Nearest(new[] { p[0], p[1], -p[2], p[3], p[4] }) <= PositionTolM) mirrorHit++;
            }
        }
        double mirrorFrac = mirrorTotal == 0 ? 1 : mirrorHit / (double)mirrorTotal;
        if (missR > 0) errors.Add($"вершин эталона без пары: {missR} из {totalR}");
        if (missA > 0) errors.Add($"записанных вершин без пары в эталоне: {missA} из {mirrorTotal}");
        if (uvMissR > 0) errors.Add($"вершин эталона без пары с той же UV (±{UvTol}): {uvMissR} из {totalR}");
        if (mirrorFrac >= 0.5) errors.Add($"зеркальная ось совпала у {mirrorFrac:P1} вершин — сверка нечувствительна");

        var tilesA = a.Triangles.GroupBy(kv => kv.Key.Tile).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
        var tilesR = r.Triangles.GroupBy(kv => kv.Key.Tile).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
        foreach (var t in tilesA.Keys.Union(tilesR.Keys))
        {
            if (tilesA.GetValueOrDefault(t) != tilesR.GetValueOrDefault(t))
                errors.Add($"тайл {t}: треугольников {tilesA.GetValueOrDefault(t)}, эталон {tilesR.GetValueOrDefault(t)}");
        }
        if (!a.Samplers.SetEquals(r.Samplers))
            errors.Add($"samplers {string.Join(";", a.Samplers)} / эталон {string.Join(";", r.Samplers)}");
        var na = a.Nodes.Select(Norm).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var nr = r.Nodes.Select(Norm).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (!na.SequenceEqual(nr)) errors.Add($"узлы {string.Join(",", na)} / эталон {string.Join(",", nr)}");
        if (!a.ExtensionsUsed.SequenceEqual(r.ExtensionsUsed))
            errors.Add($"extensionsUsed {string.Join(",", a.ExtensionsUsed)} / {string.Join(",", r.ExtensionsUsed)}");
        errors.AddRange(a.Problems.Select(p => "запись: " + p));
        errors.AddRange(r.Problems.Select(p => "эталон: " + p));

        stats = string.Create(CultureInfo.InvariantCulture,
            $"keys {a.Materials.Count}/{r.Materials.Count} tris {a.Triangles.Values.Sum()}/{r.Triangles.Values.Sum()} " +
            $"alpha {string.Join(",", a.Materials.Values.GroupBy(m => m.AlphaMode).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count()))} " +
            $"verts ref {totalR} miss {missR}, mine miss {missA}, uv miss {uvMissR}, max dev {maxDev * 1000:0.###} mm, mirror {mirrorFrac:P2}, " +
            $"samplers {string.Join(";", a.Samplers)}, nodes {na.Count}");
        return errors;
    }

    sealed record MatInfo(string AlphaMode, double Cutoff, double[] BaseColor, double Metallic, double Roughness, bool DoubleSided,
                          string? Base, string? Mr, string? Normal);

    /// <summary>Разбор glTF (JSON + bin) по ключу (материал без .NNN, тайл) из extras.agr материала.</summary>
    sealed class GltfSummary
    {
        public Dictionary<(string Mat, int Tile), MatInfo> Materials { get; } = new();
        public Dictionary<(string Mat, int Tile), long> Triangles { get; } = new();
        public Dictionary<(string Mat, int Tile), List<float[]>> Positions { get; } = new();
        public HashSet<string> Samplers { get; } = new(StringComparer.Ordinal);
        public List<string> Nodes { get; } = new();
        public List<string> ExtensionsUsed { get; } = new();
        public List<string> Problems { get; } = new();

        public static GltfSummary Load(string path)
        {
            var s = new GltfSummary();
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var g = doc.RootElement;
            string dir = Path.GetDirectoryName(path)!;
            JsonElement Arr(string name) => g.TryGetProperty(name, out var e) ? e : JsonDocument.Parse("[]").RootElement;

            if (g.TryGetProperty("extensionsUsed", out var eu)) s.ExtensionsUsed.AddRange(eu.EnumerateArray().Select(e => e.GetString()!));
            var images = Arr("images").EnumerateArray()
                .Select(i => Path.GetFileName(Uri.UnescapeDataString(i.GetProperty("uri").GetString()!))).ToList();
            var samplers = Arr("samplers").EnumerateArray().Select(sm => string.Join("/",
                sm.TryGetProperty("magFilter", out var mg) ? mg.GetInt32() : 0, sm.TryGetProperty("minFilter", out var mn) ? mn.GetInt32() : 0,
                sm.TryGetProperty("wrapS", out var ws) ? ws.GetInt32() : 10497, sm.TryGetProperty("wrapT", out var wt) ? wt.GetInt32() : 10497)).ToList();
            var textures = Arr("textures").EnumerateArray().ToList();
            string? Texture(JsonElement holder, string prop)
            {
                if (!holder.TryGetProperty(prop, out var ti)) return null;
                var t = textures[ti.GetProperty("index").GetInt32()];
                s.Samplers.Add(t.TryGetProperty("sampler", out var si) ? samplers[si.GetInt32()] : "0/0/10497/10497");
                if (ti.TryGetProperty("texCoord", out var tc) && tc.GetInt32() != 0) s.Problems.Add($"{prop}: texCoord {tc.GetInt32()}");
                return images[t.GetProperty("source").GetInt32()];
            }

            var matKeys = new List<(string, int)>();
            foreach (var m in Arr("materials").EnumerateArray())
            {
                var agr = m.GetProperty("extras").GetProperty("agr");
                var key = (Norm(agr.GetProperty("source_material").GetString()!), agr.GetProperty("udim_tile").GetInt32());
                matKeys.Add(key);
                var pbr = m.TryGetProperty("pbrMetallicRoughness", out var p) ? p : JsonDocument.Parse("{}").RootElement;
                var info = new MatInfo(
                    m.TryGetProperty("alphaMode", out var am) ? am.GetString()! : "OPAQUE",
                    m.TryGetProperty("alphaCutoff", out var ac) ? ac.GetDouble() : 0.5,
                    pbr.TryGetProperty("baseColorFactor", out var bc) ? bc.EnumerateArray().Select(x => x.GetDouble()).ToArray() : new[] { 1.0, 1, 1, 1 },
                    pbr.TryGetProperty("metallicFactor", out var mf) ? mf.GetDouble() : 1,
                    pbr.TryGetProperty("roughnessFactor", out var rf) ? rf.GetDouble() : 1,
                    m.TryGetProperty("doubleSided", out var ds) && ds.GetBoolean(),
                    Texture(pbr, "baseColorTexture"), Texture(pbr, "metallicRoughnessTexture"), Texture(m, "normalTexture"));
                if (s.Materials.TryGetValue(key, out var prev) && !SameMat(prev, info)) s.Problems.Add($"{key}: два разных материала на ключ");
                s.Materials[key] = info;
            }

            foreach (var n in Arr("nodes").EnumerateArray())
            {
                s.Nodes.Add(n.TryGetProperty("name", out var nm) ? nm.GetString()! : "");
                foreach (var trs in new[] { "matrix", "translation", "rotation", "scale" })
                {
                    if (n.TryGetProperty(trs, out _)) s.Problems.Add($"узел {s.Nodes[^1]}: {trs}");
                }
            }

            byte[] bin = File.ReadAllBytes(Path.Combine(dir, Uri.UnescapeDataString(Arr("buffers")[0].GetProperty("uri").GetString()!)));
            var accessors = Arr("accessors");
            var views = Arr("bufferViews");
            foreach (var mesh in Arr("meshes").EnumerateArray())
            {
                foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
                {
                    if (prim.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4) s.Problems.Add($"mode {mode.GetInt32()}");
                    var key = matKeys[prim.GetProperty("material").GetInt32()];
                    var idx = accessors[prim.GetProperty("indices").GetInt32()];
                    s.Triangles[key] = s.Triangles.GetValueOrDefault(key) + idx.GetProperty("count").GetInt32() / 3;
                    var acc = accessors[prim.GetProperty("attributes").GetProperty("POSITION").GetInt32()];
                    if (acc.GetProperty("componentType").GetInt32() != 5126) s.Problems.Add("POSITION не float32");
                    var view = views[acc.GetProperty("bufferView").GetInt32()];
                    int offset = (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
                                 + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
                    int stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 12;
                    int count = acc.GetProperty("count").GetInt32();
                    var uvAcc = accessors[prim.GetProperty("attributes").GetProperty("TEXCOORD_0").GetInt32()];
                    if (uvAcc.GetProperty("componentType").GetInt32() != 5126) s.Problems.Add("TEXCOORD_0 не float32");
                    if (uvAcc.GetProperty("count").GetInt32() != count) s.Problems.Add("TEXCOORD_0: число вершин не как у POSITION");
                    var uvView = views[uvAcc.GetProperty("bufferView").GetInt32()];
                    int uvOffset = (uvView.TryGetProperty("byteOffset", out var uvo) ? uvo.GetInt32() : 0)
                                   + (uvAcc.TryGetProperty("byteOffset", out var uao) ? uao.GetInt32() : 0);
                    int uvStride = uvView.TryGetProperty("byteStride", out var ubs) ? ubs.GetInt32() : 8;
                    if (!s.Positions.TryGetValue(key, out var list)) s.Positions[key] = list = new List<float[]>();
                    for (int i = 0; i < count; i++)
                    {
                        int o = offset + i * stride;
                        int ou = uvOffset + i * uvStride;
                        list.Add(new[]
                        {
                            BitConverter.ToSingle(bin, o), BitConverter.ToSingle(bin, o + 4), BitConverter.ToSingle(bin, o + 8),
                            BitConverter.ToSingle(bin, ou), BitConverter.ToSingle(bin, ou + 4),
                        });
                    }
                }
            }
            return s;
        }

        static bool SameMat(MatInfo x, MatInfo y) =>
            x.AlphaMode == y.AlphaMode && x.Base == y.Base && x.Mr == y.Mr && x.Normal == y.Normal
            && x.BaseColor.Zip(y.BaseColor).All(t => Math.Abs(t.First - t.Second) <= FactorTol)
            && Math.Abs(x.Metallic - y.Metallic) <= FactorTol && Math.Abs(x.Roughness - y.Roughness) <= FactorTol;
    }

    /// <summary>
    /// Решётка по позиции (точка — x, y, z, u, v) с ячейкой = допуску: ближайшая точка ищется в 27 соседних ячейках
    /// (как в ReferenceTests); с <c>uvTol</c> — только среди точек с той же UV.
    /// </summary>
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

        public double Nearest(float[] p, double uvTol = double.PositiveInfinity)
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
                    if (Math.Abs(p[3] - q[3]) > uvTol || Math.Abs(p[4] - q[4]) > uvTol) continue;
                    double ddx = p[0] - q[0], ddy = p[1] - q[1], ddz = p[2] - q[2];
                    double d = Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                    if (d < best) best = d;
                }
            }
            return best;
        }
    }
}
