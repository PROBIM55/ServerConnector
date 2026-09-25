using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Connector.AgrConversion;

/// <summary>Треугольный примитив glTF: мировые позиции (метры, оси glTF), сырые значения позиций и индексы.</summary>
public sealed class AgrGeomPrimitive
{
    public required int Mesh { get; init; }
    public int? Material { get; init; }

    /// <summary>x, y, z вершин после нормализации и мировой матрицы узла.</summary>
    public required double[] Positions { get; init; }

    /// <summary>Значения позиций как в буфере (целые при квантовании, float без него) — для счёта вырожденных.</summary>
    public required double[] RawPositions { get; init; }

    public float[]? Uvs { get; init; }
    public required int[] Indices { get; init; }
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Минимальный читатель геометрии glTF/GLB без сжатия: треугольники мешей сцены с мировыми матрицами узлов,
/// типы компонент 5120–5126 (в том числе нормализованные — KHR_mesh_quantization). Сжатые буферы
/// (EXT_meshopt_compression) и sparse-аксессоры не читает — для них <see cref="InvalidDataException"/>.
/// Используется для замера ошибок уровней по «двойнику» gltfpack без сжатия и для промежуточного glTF части.
/// </summary>
public sealed class AgrGlbGeometry
{
    const uint GlbMagic = 0x46546C67; // "glTF"
    const uint ChunkJson = 0x4E4F534A;
    const uint ChunkBin = 0x004E4942;

    readonly string _baseDir;
    readonly byte[]? _glbBin;
    readonly Dictionary<int, byte[]> _buffers = new();

    public JsonObject Json { get; }
    public long FileBytes { get; }

    AgrGlbGeometry(JsonObject json, byte[]? glbBin, string baseDir, long fileBytes)
    {
        Json = json;
        _glbBin = glbBin;
        _baseDir = baseDir;
        FileBytes = fileBytes;
    }

    /// <summary>Читает .glb (JSON + BIN) или .gltf (внешние буферы по URI рядом с файлом).</summary>
    public static AgrGlbGeometry Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == GlbMagic)
        {
            var (json, bin) = SplitGlb(bytes, path);
            return new AgrGlbGeometry(json, bin, dir, bytes.Length);
        }
        var node = JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException($"{path}: не JSON-объект glTF");
        return new AgrGlbGeometry(node, null, dir, bytes.Length);
    }

    /// <summary>Только JSON-часть GLB (без чтения буферов в объекты) — для быстрых проверок расширений и счётчиков.</summary>
    public static JsonObject ReadJsonOnly(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[20];
        fs.ReadExactly(head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != GlbMagic)
        {
            fs.Position = 0;
            return JsonNode.Parse(fs) as JsonObject ?? throw new InvalidDataException($"{path}: не glTF");
        }
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
        if (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(16)) != ChunkJson)
        {
            throw new InvalidDataException($"{path}: первый блок GLB не JSON");
        }
        var json = new byte[len];
        fs.ReadExactly(json);
        return JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException($"{path}: не glTF");
    }

    static (JsonObject Json, byte[]? Bin) SplitGlb(byte[] b, string path)
    {
        int pos = 12;
        JsonObject? json = null;
        byte[]? bin = null;
        while (pos + 8 <= b.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos + 4));
            if (pos + 8 + len > b.Length)
            {
                throw new InvalidDataException($"{path}: блок GLB за концом файла");
            }
            if (type == ChunkJson)
            {
                json = JsonNode.Parse(Encoding.UTF8.GetString(b, pos + 8, len)) as JsonObject;
            }
            else if (type == ChunkBin && bin == null)
            {
                bin = b.AsSpan(pos + 8, len).ToArray();
            }
            pos += 8 + len;
        }
        return (json ?? throw new InvalidDataException($"{path}: в GLB нет JSON"), bin);
    }

    byte[] Buffer(int index)
    {
        if (_buffers.TryGetValue(index, out var data))
        {
            return data;
        }
        var buf = Json["buffers"]![index]!.AsObject();
        string? uri = buf["uri"]?.GetValue<string>();
        if (uri == null)
        {
            data = _glbBin ?? throw new InvalidDataException("буфер без URI, а блока BIN нет");
        }
        else if (uri.StartsWith("data:", StringComparison.Ordinal))
        {
            data = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
        }
        else
        {
            data = File.ReadAllBytes(Path.Combine(_baseDir, Uri.UnescapeDataString(uri)));
        }
        _buffers[index] = data;
        return data;
    }

    /// <summary>Аксессор → значения double по компонентам; <paramref name="normalize"/> — применять normalized.</summary>
    public double[] ReadAccessor(int index, bool normalize, out int components)
    {
        var acc = Json["accessors"]![index]!.AsObject();
        if (acc["sparse"] != null)
        {
            throw new InvalidDataException($"аксессор {index}: sparse не поддержан");
        }
        int count = acc["count"]!.GetValue<int>();
        components = acc["type"]!.GetValue<string>() switch
        {
            "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16,
            var t => throw new InvalidDataException($"аксессор {index}: тип {t}"),
        };
        int ct = acc["componentType"]!.GetValue<int>();
        int size = ct switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new InvalidDataException($"componentType {ct}") };
        bool normalized = normalize && (acc["normalized"]?.GetValue<bool>() ?? false);
        var result = new double[count * components];
        if (acc["bufferView"] == null)
        {
            return result;
        }
        var view = Json["bufferViews"]![acc["bufferView"]!.GetValue<int>()]!.AsObject();
        if (view["extensions"]?["EXT_meshopt_compression"] != null || view["extensions"]?["KHR_meshopt_compression"] != null)
        {
            throw new InvalidDataException("буфер сжат EXT_meshopt_compression — читать двойника без сжатия");
        }
        byte[] data = Buffer(view["buffer"]!.GetValue<int>());
        int stride = view["byteStride"]?.GetValue<int>() ?? size * components;
        int start = (view["byteOffset"]?.GetValue<int>() ?? 0) + (acc["byteOffset"]?.GetValue<int>() ?? 0);
        var span = data.AsSpan();
        for (int i = 0; i < count; i++)
        {
            int o = start + i * stride;
            for (int c = 0; c < components; c++)
            {
                int p = o + c * size;
                double v = ct switch
                {
                    5120 => (sbyte)span[p],
                    5121 => span[p],
                    5122 => BinaryPrimitives.ReadInt16LittleEndian(span[p..]),
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(span[p..]),
                    5125 => BinaryPrimitives.ReadUInt32LittleEndian(span[p..]),
                    _ => BinaryPrimitives.ReadSingleLittleEndian(span[p..]),
                };
                if (normalized)
                {
                    v = ct switch
                    {
                        5120 => Math.Max(v / 127.0, -1), 5121 => v / 255.0, 5122 => Math.Max(v / 32767.0, -1), 5123 => v / 65535.0, _ => v,
                    };
                }
                result[i * components + c] = v;
            }
        }
        return result;
    }

    /// <summary>Треугольники всех мешей сцены (каждое вхождение узла — отдельно) в мировых координатах glTF.</summary>
    public List<AgrGeomPrimitive> Primitives(bool withUv = false)
    {
        var list = new List<AgrGeomPrimitive>();
        var nodes = Json["nodes"]?.AsArray();
        var scenes = Json["scenes"]?.AsArray();
        if (nodes == null || scenes == null || scenes.Count == 0)
        {
            return list;
        }
        int sceneIndex = Json["scene"]?.GetValue<int>() ?? 0;
        var stack = new Stack<(int Node, double[] Parent)>();
        foreach (var n in scenes[sceneIndex]!["nodes"]?.AsArray() ?? new JsonArray())
        {
            stack.Push((n!.GetValue<int>(), Identity()));
        }
        while (stack.Count > 0)
        {
            var (ni, parent) = stack.Pop();
            var node = nodes[ni]!.AsObject();
            double[] world = Mul(parent, LocalMatrix(node));
            if (node["mesh"] != null)
            {
                int mi = node["mesh"]!.GetValue<int>();
                foreach (var prim in Json["meshes"]![mi]!["primitives"]!.AsArray())
                {
                    var p = prim!.AsObject();
                    int mode = p["mode"]?.GetValue<int>() ?? 4;
                    if (mode != 4)
                    {
                        continue;
                    }
                    var attrs = p["attributes"]!.AsObject();
                    double[] raw = ReadAccessor(attrs["POSITION"]!.GetValue<int>(), normalize: false, out _);
                    double[] pos = ReadAccessor(attrs["POSITION"]!.GetValue<int>(), normalize: true, out _);
                    for (int v = 0; v < pos.Length; v += 3)
                    {
                        double x = pos[v], y = pos[v + 1], z = pos[v + 2];
                        pos[v] = world[0] * x + world[4] * y + world[8] * z + world[12];
                        pos[v + 1] = world[1] * x + world[5] * y + world[9] * z + world[13];
                        pos[v + 2] = world[2] * x + world[6] * y + world[10] * z + world[14];
                    }
                    int[] idx;
                    if (p["indices"] != null)
                    {
                        idx = Array.ConvertAll(ReadAccessor(p["indices"]!.GetValue<int>(), false, out _), d => (int)d);
                    }
                    else
                    {
                        idx = Enumerable.Range(0, pos.Length / 3).ToArray();
                    }
                    float[]? uv = null;
                    if (withUv && attrs["TEXCOORD_0"] != null)
                    {
                        uv = Array.ConvertAll(ReadAccessor(attrs["TEXCOORD_0"]!.GetValue<int>(), true, out _), d => (float)d);
                    }
                    list.Add(new AgrGeomPrimitive
                    {
                        Mesh = mi, Material = p["material"]?.GetValue<int>(), Positions = pos, RawPositions = raw, Uvs = uv, Indices = idx,
                    });
                }
            }
            foreach (var c in node["children"]?.AsArray() ?? new JsonArray())
            {
                stack.Push((c!.GetValue<int>(), world));
            }
        }
        return list;
    }

    static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    static double[] LocalMatrix(JsonObject node)
    {
        if (node["matrix"] is JsonArray m)
        {
            return m.Select(v => v!.GetValue<double>()).ToArray();
        }
        double[] t = node["translation"] is JsonArray ta ? ta.Select(v => v!.GetValue<double>()).ToArray() : new double[3];
        double[] r = node["rotation"] is JsonArray ra ? ra.Select(v => v!.GetValue<double>()).ToArray() : new double[] { 0, 0, 0, 1 };
        double[] s = node["scale"] is JsonArray sa ? sa.Select(v => v!.GetValue<double>()).ToArray() : new double[] { 1, 1, 1 };
        double x = r[0], y = r[1], z = r[2], w = r[3];
        // столбцовый порядок glTF: M = T · R · S
        return new[]
        {
            (1 - 2 * (y * y + z * z)) * s[0], (2 * (x * y + z * w)) * s[0], (2 * (x * z - y * w)) * s[0], 0,
            (2 * (x * y - z * w)) * s[1], (1 - 2 * (x * x + z * z)) * s[1], (2 * (y * z + x * w)) * s[1], 0,
            (2 * (x * z + y * w)) * s[2], (2 * (y * z - x * w)) * s[2], (1 - 2 * (x * x + y * y)) * s[2], 0,
            t[0], t[1], t[2], 1,
        };
    }

    static double[] Mul(double[] a, double[] b)
    {
        var r = new double[16];
        for (int c = 0; c < 4; c++)
        {
            for (int row = 0; row < 4; row++)
            {
                double sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += a[k * 4 + row] * b[c * 4 + k];
                }
                r[c * 4 + row] = sum;
            }
        }
        return r;
    }

    /// <summary>Картинки GLB: MIME и размер KTX2 из заголовка (pixelWidth/pixelHeight), если картинка в BIN.</summary>
    public List<(string Mime, int Width, int Height, int Bytes)> Images()
    {
        var list = new List<(string, int, int, int)>();
        foreach (var im in Json["images"]?.AsArray() ?? new JsonArray())
        {
            string mime = im!["mimeType"]?.GetValue<string>() ?? "";
            int w = 0, h = 0, bytes = 0;
            if (im["bufferView"] != null)
            {
                var view = Json["bufferViews"]![im["bufferView"]!.GetValue<int>()]!.AsObject();
                byte[] data = Buffer(view["buffer"]!.GetValue<int>());
                int off = view["byteOffset"]?.GetValue<int>() ?? 0;
                bytes = view["byteLength"]!.GetValue<int>();
                if (mime == "image/ktx2" && bytes >= 28)
                {
                    w = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 20));
                    h = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 24));
                }
            }
            list.Add((mime, w, h, bytes));
        }
        return list;
    }
}
