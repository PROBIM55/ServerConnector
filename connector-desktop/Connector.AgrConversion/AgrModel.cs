using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Connector.AgrConversion;

/// <summary>Сетка модели части: координаты в осях <see cref="AgrRules.FrameDescription"/>.</summary>
public sealed class AgrMesh
{
    public required string Node { get; init; }
    public required string Material { get; init; }

    /// <summary>x, y, z вершин, метры, трансформации узлов запечены.</summary>
    public required float[] Positions { get; init; }

    /// <summary>Нормали вершин (единичные) или null, если в FBX их нет.</summary>
    public float[]? Normals { get; init; }

    /// <summary>UV канала 0 как в FBX (v вверх); для glTF — (u, 1 − v). Null, если UV нет.</summary>
    public float[]? Uvs { get; init; }

    /// <summary>Только треугольники, по три индекса.</summary>
    public required int[] Indices { get; init; }

    /// <summary>UDIM-тайл каждой грани (по центроиду UV).</summary>
    public required int[] FaceTiles { get; init; }

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

public sealed class AgrBounds
{
    public double[] Min { get; init; } = { double.MaxValue, double.MaxValue, double.MaxValue };
    public double[] Max { get; init; } = { double.MinValue, double.MinValue, double.MinValue };

    public double[] Size => new[] { Max[0] - Min[0], Max[1] - Min[1], Max[2] - Min[2] };
    public double[] Center => new[] { (Max[0] + Min[0]) / 2, (Max[1] + Min[1]) / 2, (Max[2] + Min[2]) / 2 };

    [JsonIgnore]
    public bool IsEmpty => Min[0] > Max[0];

    public void Add(double x, double y, double z)
    {
        if (x < Min[0]) Min[0] = x;
        if (y < Min[1]) Min[1] = y;
        if (z < Min[2]) Min[2] = z;
        if (x > Max[0]) Max[0] = x;
        if (y > Max[1]) Max[1] = y;
        if (z > Max[2]) Max[2] = z;
    }
}

/// <summary>Материал + UDIM-тайл, на который легли грани.</summary>
public sealed class AgrTileInfo
{
    public required string Material { get; init; }

    /// <summary>"T_&lt;stem&gt;_*_&lt;set&gt;", "паспорт Glasses" или null, если набор не найден.</summary>
    public string? Set { get; set; }
    public required int Tile { get; init; }
    public int Faces { get; set; }

    /// <summary>real — есть настоящая карта; stub — только однотонные; glass — стекло по паспорту.</summary>
    public string Class { get; set; } = "";

    /// <summary>Вид карты → "real WxH" / "stub rgb[r, g, b]" / null (карты нет), как в эталоне B1.</summary>
    public SortedDictionary<string, string?> Maps { get; } = new(StringComparer.Ordinal);

    /// <summary>Вид карты → имя файла текстуры.</summary>
    public SortedDictionary<string, string?> MapFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>Канал → путь картинки внутри папки части (<see cref="AgrTextureInfo.RelativePath"/>): ключ текстуры
    /// для записи glTF — одноимённые файлы из разных подпапок не смешиваются (ревью C1b).</summary>
    public SortedDictionary<string, string> MapPaths { get; } = new(StringComparer.Ordinal);
}

public sealed class AgrAlphaStats
{
    public double Mean { get; init; }
    public int Min255 { get; init; }
    public double FracBelowHalf { get; init; }
    public double FracMid { get; init; }

    /// <summary>Альфа не тривиальна: минимум меньше 254/255 (правило B1).</summary>
    public bool NonTrivial { get; init; }

    /// <summary>OPAQUE / MASK (почти двоичная, промежуточных &lt; 5 %) / BLEND — подсказка для C1b.</summary>
    public string Mode { get; init; } = "OPAQUE";
}

/// <summary>R-канал карты ERM (emissive): в glTF не переносится, пишется в «отброшено».</summary>
public sealed class AgrErmRedStats
{
    public double Mean255 { get; init; }
    public int Max255 { get; init; }
    public double FracAbove005 { get; init; }

    /// <summary>Несёт emissive: неоднотонная — доля R &gt; 0,05 больше 0,1 %; однотонная — R &gt; 12 (правила B1).</summary>
    public bool CarriesEmissive { get; init; }
}

public sealed class AgrTextureInfo
{
    public required string File { get; init; }
    public required string RelativePath { get; init; }
    public required string Stem { get; init; }
    public required string Kind { get; init; }
    public required string KindRaw { get; init; }
    public required string Set { get; init; }
    public required int Tile { get; init; }
    public long Bytes { get; init; }
    public int W { get; set; }
    public int H { get; set; }
    public int? BitDepth { get; set; }
    public int? ColorType { get; set; }
    public string PixelFormat { get; set; } = "";
    public double[] Mean { get; set; } = Array.Empty<double>();
    public int[] Min255 { get; set; } = Array.Empty<int>();
    public int[] Max255 { get; set; } = Array.Empty<int>();
    public double Span255 { get; set; }
    public bool Uniform { get; set; }
    public int[] Rgb255 { get; set; } = Array.Empty<int>();
    public bool HasAlpha { get; set; }
    public AgrAlphaStats? Alpha { get; set; }
    public AgrErmRedStats? ErmRed { get; set; }
    public bool Analyzed { get; set; }
    public int UsedFaces { get; set; }

    /// <summary>image — настоящая картинка; const — однотонная заглушка (константа); unused — тайл без граней.</summary>
    public string Role { get; set; } = "unused";
    public double Ms { get; set; }
}

public sealed class AgrCollisionStat
{
    public int Objects { get; set; }
    public int Triangles { get; set; }

    /// <summary>Вершины после JoinIdenticalVertices assimp (не равны числу вершин Blender).</summary>
    public int VerticesAssimp { get; set; }
}

public sealed class AgrEmissiveEntry
{
    public required string File { get; init; }
    public int Tile { get; init; }
    public int UsedFaces { get; init; }
    public bool Uniform { get; init; }
    public required AgrErmRedStats Red { get; init; }
}

/// <summary>Всё, что не переносится в модель, — с причиной.</summary>
public sealed class AgrDropped
{
    public SortedDictionary<string, AgrCollisionStat> Collision { get; } = new(StringComparer.Ordinal);
    public List<string> LightFbx { get; } = new();
    public List<string> IgnoredFiles { get; } = new();
    public List<string> UnusedTextures { get; } = new();
    public List<AgrEmissiveEntry> Emissive { get; } = new();
    public string EmissiveRule { get; init; } =
        "R карты ERM (emissive) в модель не переносится; G — roughness, B — metallic";
    public int NonTriangleFaces { get; set; }
}

public sealed class AgrPreview
{
    public required string File { get; init; }
    public int Bytes { get; init; }
    public required string Format { get; init; }
    public int[]? Size { get; init; }
    public int Base64Chars { get; init; }

    [JsonIgnore]
    public byte[] Data { get; init; } = Array.Empty<byte>();
}

public sealed class AgrPassportFeature
{
    public required string Source { get; init; }
    public int Index { get; init; }
    public string? Type { get; init; }

    /// <summary>Поля паспорта как в geojson, без imageBase64 (превью — отдельно).</summary>
    public required JsonObject Properties { get; init; }
    public JsonNode? Geometry { get; init; }

    /// <summary>Прочие поля объекта (например, Glasses).</summary>
    public required JsonObject FeatureExtra { get; init; }
    public AgrPreview? Preview { get; init; }
}

public sealed class AgrPassport
{
    public required string Part { get; init; }
    public List<string> GeojsonFiles { get; } = new();
    public List<AgrPassportFeature> Features { get; } = new();

    /// <summary>Поля FeatureCollection, кроме features.</summary>
    public JsonObject Collection { get; set; } = new();

    /// <summary>Точка МСК-77: [x — восток, y — север] (первая координата geojson — x).</summary>
    public double[]? Point { get; set; }
    public string PointCrsNote { get; set; } = "";

    /// <summary>Стекло по паспорту: материал → свойства (Glasses).</summary>
    public SortedDictionary<string, JsonObject> Glasses { get; } = new(StringComparer.Ordinal);
}

/// <summary>Размещение части (Р6): X = x + E, Y = y + N, Z = z + DZ.</summary>
public sealed class AgrPlacement
{
    public double E { get; init; }
    public double N { get; init; }
    public double? DZ { get; init; }
    public string Rule { get; init; } =
        "МСК-77 (Z вверх): X = x + E, Y = y + N, Z = z + DZ; E, N — точка geojson, DZ — h_relief паспорта";
}

/// <summary>Разобранная часть АГР.</summary>
public sealed class AgrPart
{
    public required string Name { get; init; }
    public required string FbxFile { get; init; }
    public string? Source { get; init; }
    public string Frame { get; init; } = AgrRules.FrameDescription;
    public string AssimpVersion { get; init; } = "";
    public string ImportOptions { get; init; } = "";
    public SortedDictionary<string, string> FbxSettings { get; init; } = new(StringComparer.Ordinal);
    public double[] RootTransform { get; init; } = Array.Empty<double>();

    [JsonIgnore]
    public List<AgrMesh> Meshes { get; } = new();

    public AgrBounds Bounds { get; } = new();
    public List<AgrTileInfo> Tiles { get; } = new();
    public List<AgrTextureInfo> Textures { get; } = new();
    public AgrPassport? Passport { get; set; }
    public AgrPlacement? Placement { get; set; }
    public AgrDropped Dropped { get; } = new();
    public int StraddleFaces { get; set; }
    public double StraddleMaxOverflow { get; set; }
    public List<string> Warnings { get; } = new();
    public SortedDictionary<string, double> TimingsS { get; } = new(StringComparer.Ordinal);

    public int ModelTriangles => Meshes.Sum(m => m.TriangleCount);

    /// <summary>"материал|тайл" → число граней.</summary>
    public SortedDictionary<string, int> TrianglesByTile()
    {
        var d = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in Tiles)
        {
            d[t.Material + "|" + t.Tile] = t.Faces;
        }
        return d;
    }
}
