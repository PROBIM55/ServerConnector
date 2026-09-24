using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connector.AgrConversion;

/// <summary>
/// JSON-отчёт по части: треугольники всего и по тайлам, габарит, тайлы и их классы, заглушки, альфа, отброшенное
/// (UCX_, свет, emissive ERM), паспорт, предупреждения, время. Поля — snake_case, как в эталоне B1.
/// </summary>
public static class AgrReport
{
    public const int Version = 1;

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    static double[] R(double[] v, int digits = 6) => Array.ConvertAll(v, x => Math.Round(x, digits));

    public static object Build(AgrPart p)
    {
        var byTile = new SortedDictionary<int, int>();
        foreach (var t in p.Tiles)
        {
            byTile.TryGetValue(t.Tile, out int c);
            byTile[t.Tile] = c + t.Faces;
        }
        int Faces(string cls) => p.Tiles.Where(t => t.Class == cls).Sum(t => t.Faces);
        var stubs = p.Textures.Where(t => t.Analyzed && t.Uniform && t.UsedFaces > 0)
            .Select(t => new { file = t.File, kind = t.Kind, tile = t.Tile, rgb255 = t.Rgb255, span255 = t.Span255, used_faces = t.UsedFaces })
            .ToList();
        var alpha = p.Textures.Where(t => t.Alpha is { NonTrivial: true })
            .Select(t => new { file = t.File, kind = t.Kind, tile = t.Tile, used_faces = t.UsedFaces, alpha = t.Alpha })
            .ToList();
        var passport = p.Passport == null ? null : new
        {
            geojson_files = p.Passport.GeojsonFiles,
            features = p.Passport.Features.Count,
            point = p.Passport.Point,
            point_note = p.Passport.PointCrsNote,
            h_relief = AgrGeoJsonReader.HRelief(p.Passport),
            glasses = p.Passport.Glasses.Keys.ToList(),
            previews = p.Passport.Features.Where(f => f.Preview != null).Select(f => f.Preview).ToList(),
            properties = p.Passport.Features.Select(f => f.Properties).ToList(),
        };
        return new
        {
            report_version = Version,
            part = p.Name,
            source = p.Source,
            fbx_file = p.FbxFile,
            frame = p.Frame,
            assimp = p.AssimpVersion,
            import_options = p.ImportOptions,
            fbx_settings = p.FbxSettings,
            root_transform = p.RootTransform,
            triangles = new
            {
                model = p.ModelTriangles,
                model_verts_assimp = p.Meshes.Sum(m => m.VertexCount),
                meshes = p.Meshes.Count,
                by_tile = byTile,
                by_material_tile = p.TrianglesByTile(),
                collision = p.Dropped.Collision,
            },
            bbox_model_m = p.Bounds.IsEmpty ? null : new
            {
                min = R(p.Bounds.Min), max = R(p.Bounds.Max), center = R(p.Bounds.Center), size = R(p.Bounds.Size),
            },
            straddle_faces = p.StraddleFaces,
            straddle_max_overflow_uv = p.StraddleMaxOverflow,
            tiles = p.Tiles,
            tiles_summary = new
            {
                used = p.Tiles.Count,
                real = p.Tiles.Count(t => t.Class == "real"),
                stub_only = p.Tiles.Count(t => t.Class == "stub"),
                glass = p.Tiles.Count(t => t.Class == "glass"),
                faces_on_real = Faces("real"),
                faces_on_stub_only = Faces("stub"),
                faces_on_glass = Faces("glass"),
            },
            textures_summary = new
            {
                files = p.Textures.Count,
                analyzed = p.Textures.Count(t => t.Analyzed),
                real = p.Textures.Count(t => t.Role == "image"),
                uniform = p.Textures.Count(t => t.Analyzed && t.Uniform),
                unused = p.Textures.Count(t => t.Role == "unused"),
                bit_depths = p.Textures.Where(t => t.BitDepth != null).GroupBy(t => t.BitDepth!.Value)
                    .ToDictionary(g => g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), g => g.Count()),
            },
            stubs,
            alpha,
            textures = p.Textures,
            dropped = p.Dropped,
            passport,
            placement = p.Placement,
            warnings = p.Warnings,
            timings_s = p.TimingsS,
        };
    }

    public static string ToJson(AgrPart part) => JsonSerializer.Serialize(Build(part), Options);

    public static void Write(AgrPart part, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(part), new UTF8Encoding(false));
    }
}
