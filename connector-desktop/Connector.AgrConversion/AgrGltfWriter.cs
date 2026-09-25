using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;

namespace Connector.AgrConversion;

/// <summary>Настройки записи промежуточного glTF части.</summary>
public sealed class AgrGltfWriteOptions
{
    /// <summary>
    /// Лежат ли картинка (аргумент 1) и папка вывода (аргумент 2) на одном томе: да — в glTF ссылка на исходник
    /// относительным путём, нет — потоковая копия картинки рядом с glTF. Null — <see cref="AgrGltfWriter.SameVolumeByRoot"/>.
    /// Подменяется в тестах, чтобы проверить случай другого тома.
    /// </summary>
    public Func<string, string, bool>? SameVolume { get; init; }
}

/// <summary>Картинка, на которую ссылается записанный glTF.</summary>
public sealed class AgrGltfImage
{
    public required string File { get; init; }

    /// <summary>Полный путь исходного PNG части.</summary>
    public required string Source { get; init; }

    /// <summary>URI в glTF: относительный, с процентным кодированием.</summary>
    public required string Uri { get; init; }

    /// <summary>Картинка скопирована рядом с glTF (другой том), иначе — ссылка на исходник.</summary>
    public bool Copied { get; init; }
    public long Bytes { get; init; }
}

/// <summary>Материал glTF: исходный материал FBX на один UDIM-тайл.</summary>
public sealed class AgrGltfMaterial
{
    public required string Name { get; init; }
    public required string SourceMaterial { get; init; }
    public int Tile { get; init; }

    /// <summary>real / stub / glass — как у тайла читателя.</summary>
    public required string Class { get; init; }

    /// <summary>OPAQUE / MASK (cutoff 0,5) / BLEND.</summary>
    public string AlphaMode { get; set; } = "OPAQUE";
    public int Faces { get; init; }

    /// <summary>Канал → «image файл» или константа, как в extras.agr.maps у S1a-1.</summary>
    public SortedDictionary<string, string> Maps { get; } = new(StringComparer.Ordinal);
}

public sealed class AgrGltfResult
{
    public required string GltfPath { get; init; }
    public required string BinPath { get; init; }

    /// <summary>Узлы вместе с корнем части.</summary>
    public int Nodes { get; set; }
    public int Meshes { get; set; }
    public int Primitives { get; set; }
    public long Triangles { get; set; }
    public long Vertices { get; set; }
    public List<AgrGltfMaterial> Materials { get; } = new();
    public List<AgrGltfImage> Images { get; } = new();

    /// <summary>Что из исходника в glTF не записано (IOR стекла и т. п.).</summary>
    public List<string> Notes { get; } = new();
    public double Seconds { get; set; }
}

/// <summary>
/// Запись промежуточного glTF 2.0 одной части (<c>&lt;часть&gt;.gltf</c> + <c>.bin</c>, картинки по URI) из
/// <see cref="AgrPart"/> — по устройству промежуточного glTF S1a-1 (<c>tiles_src</c>, экспорт Blender):
/// <list type="bullet">
/// <item>корневой узел — имя части (extras: размещение); под ним по узлу на объект FBX, у каждого своя сетка,
/// в сетке по примитиву на пару (материал, UDIM-тайл); позиции и нормали — из осей АГР в Y вверх glTF
/// (<see cref="FrameDescription"/>), без других поворотов и масштаба; UV сдвинуты в [0, 1] своего тайла, v перевёрнут;</item>
/// <item>материал на (материал, тайл), двусторонний: Diffuse → baseColorTexture, ERM → metallicRoughnessTexture
/// (G — шероховатость, B — металличность, R отброшен), Normal → normalTexture; однотонная карта — константа
/// (Diffuse sRGB → линейный, ERM: G/255 и B/255, Normal — без карты); нет карты — серый 204, шероховатость 1;</item>
/// <item>альфа Diffuse — MASK (cutoff 0,5) или BLEND по правилу B1 из статистики читателя; стекло — константы паспорта
/// Glasses (цвет, transparency как непрозрачность, roughness, metallicity), BLEND при непрозрачности меньше 1;</item>
/// <item>все текстуры — один sampler CLAMP_TO_EDGE по обеим осям, LINEAR_MIPMAP_LINEAR / LINEAR, как у S1a-1;</item>
/// <item>картинки не встраиваются и не читаются в память: тот же том — относительная ссылка на исходник с процентным
/// кодированием, другой том — потоковая копия рядом с glTF (<see cref="AgrGltfWriteOptions.SameVolume"/>).</item>
/// </list>
/// Ссылки на исходники действительны, пока жива папка части: распаковку zip удалять только после gltfpack (C1c).
/// <para>
/// Потокобезопасность: экземпляр создаётся на одну часть и не хранит изменяемого состояния (часть, папка, правило тома —
/// только для чтения; всё состояние записи — локальное внутри <see cref="Write"/>). Разные экземпляры можно запускать
/// параллельно; один экземпляр — тоже, если папки вывода разные. <see cref="AgrPart"/> во время записи не меняется и не
/// должен меняться извне; паспорт стекла (ленивые узлы JSON) читается под замком словаря Glasses части.
/// </para>
/// </summary>
public sealed class AgrGltfWriter
{
    /// <summary>Оси glTF относительно осей АГР читателя (<see cref="AgrRules.FrameDescription"/>).</summary>
    public const string FrameDescription =
        "glTF Y вверх, метры: x_gltf = x, y_gltf = z, z_gltf = −y (x — восток, y — север, z — вверх, оси АГР)";

    /// <summary>Sampler всех текстур — как у S1a-1 (EXTEND Blender): иначе REPEAT даёт швы на краях тайлов.</summary>
    public const TextureWrapMode Wrap = TextureWrapMode.CLAMP_TO_EDGE;

    static readonly Regex SafeName = new(@"^[A-Za-z0-9._\-]+$", RegexOptions.CultureInvariant);
    static readonly int[] MissingDiffuse = { 204, 204, 204 };
    static readonly int[] MissingErm = { 0, 255, 0 };

    // PNG 1x1 — заглушка содержимого картинки в модели SharpGLTF: байты исходника не читаются, файл не пишется
    // (URI возвращает колбэк записи).
    const string PlaceholderPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC";

    readonly AgrPart _part;
    readonly string _partDirectory;
    readonly Func<string, string, bool> _sameVolume;

    /// <param name="part">Разобранная часть (читатель C1a, со статистикой текстур).</param>
    /// <param name="partDirectory">Папка части, из которой читались текстуры (<see cref="AgrTextureInfo.RelativePath"/>).</param>
    public AgrGltfWriter(AgrPart part, string partDirectory, AgrGltfWriteOptions? options = null)
    {
        _part = part ?? throw new ArgumentNullException(nameof(part));
        _partDirectory = Path.GetFullPath(partDirectory ?? throw new ArgumentNullException(nameof(partDirectory)));
        _sameVolume = options?.SameVolume ?? SameVolumeByRoot;
    }

    /// <summary>Один том — одинаковый корень пути (буква диска или \\сервер\ресурс), без учёта регистра.</summary>
    public static bool SameVolumeByRoot(string sourceFile, string outputDirectory) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(sourceFile)), Path.GetPathRoot(Path.GetFullPath(outputDirectory)),
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>Относительный путь → URI glTF: сегменты через «/», каждый в процентном кодировании RFC 3986 (UTF-8).</summary>
    public static string EncodeRelativeUri(string relativePath)
    {
        var segments = relativePath.Split('\\', '/');
        return string.Join("/", segments.Select(s => s is "." or ".." ? s : Uri.EscapeDataString(s)));
    }

    /// <summary>
    /// Имя файлов glTF/bin: имя части, если оно из латиницы, цифр и «._-»; иначе — кодированное с «_» вместо «%».
    /// SharpGLTF пишет URI буфера по имени файла без кодирования, поэтому имя обязано быть безопасным.
    /// </summary>
    public static string FileStem(string partName) =>
        SafeName.IsMatch(partName) ? partName : Uri.EscapeDataString(partName).Replace('%', '_');

    /// <summary>Пишет glTF части в <paramref name="outputDirectory"/> (папка создаётся; файлы части перезаписываются).</summary>
    public AgrGltfResult Write(string outputDirectory)
    {
        var sw = Stopwatch.StartNew();
        string outDir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outDir);
        string stem = FileStem(_part.Name);
        var result = new AgrGltfResult
        {
            GltfPath = Path.Combine(outDir, stem + ".gltf"),
            BinPath = Path.Combine(outDir, stem + ".bin"),
        };

        // Ключ текстуры — путь внутри части (ревью C1b): одноимённые файлы из разных подпапок — разные картинки.
        var textures = new Dictionary<string, AgrTextureInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _part.Textures)
        {
            if (!textures.TryAdd(TextureKey(t.RelativePath), t))
            {
                throw new InvalidDataException($"{_part.Name}: картинка {t.RelativePath} встречается дважды");
            }
        }

        var model = ModelRoot.CreateModel();
        var images = new ImageSet(model, Convert.FromBase64String(PlaceholderPngBase64));
        var materials = new Dictionary<(string Material, int Tile), Material>();
        foreach (var tile in _part.Tiles)
        {
            if (tile.Faces > 0 && !materials.ContainsKey((tile.Material, tile.Tile)))
            {
                materials[(tile.Material, tile.Tile)] = BuildMaterial(model, tile, textures, images, result);
            }
        }

        var scene = model.UseScene(_part.Name);
        var root = scene.CreateNode(_part.Name);
        root.Extras = RootExtras();
        BuildNodes(model, root, materials, result);

        // Картинки: ссылки или копии — до записи glTF (glTF пишется последним).
        var uriByToken = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (token, tex) in images.Entries)
        {
            var img = PlaceImage(tex, outDir);
            uriByToken[token] = img.Uri;
            result.Images.Add(img);
        }

        var settings = new WriteSettings
        {
            ImageWriting = ResourceWriteMode.SatelliteFile,
            // Колбэк не пишет файлов: картинка уже на месте (исходник или копия), в glTF — только готовый URI.
            ImageWriteCallback = (context, assetName, image) => uriByToken.TryGetValue(assetName, out var uri)
                ? uri
                : throw new InvalidOperationException($"glTF {_part.Name}: неизвестная картинка {assetName}"),
            MergeBuffers = true,
            JsonIndented = true,
            Validation = SharpGLTF.Validation.ValidationMode.Strict,
        };
        model.SaveGLTF(result.GltfPath, settings);
        if (!File.Exists(result.BinPath))
        {
            throw new InvalidOperationException($"glTF {_part.Name}: SharpGLTF не записал {Path.GetFileName(result.BinPath)}");
        }
        result.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 3);
        return result;
    }

    JsonNode RootExtras()
    {
        var extras = new JsonObject
        {
            ["part"] = _part.Name,
            ["frame"] = FrameDescription,
            ["units"] = "m",
        };
        if (_part.Placement is { } pl)
        {
            extras["placement"] = new JsonObject { ["E"] = pl.E, ["N"] = pl.N, ["dZ"] = pl.DZ };
            extras["placement_rule"] = pl.Rule;
        }
        return extras;
    }

    Material BuildMaterial(ModelRoot model, AgrTileInfo tile, Dictionary<string, AgrTextureInfo> textures, ImageSet images,
                           AgrGltfResult result)
    {
        var info = new AgrGltfMaterial
        {
            Name = tile.Material + "_" + tile.Tile.ToString(CultureInfo.InvariantCulture),
            SourceMaterial = tile.Material,
            Tile = tile.Tile,
            Class = tile.Class,
            Faces = tile.Faces,
        };
        result.Materials.Add(info);
        var mat = model.CreateMaterial(info.Name);

        if (tile.Class == "glass")
        {
            var g = GlassOf(tile.Material)
                    ?? throw new InvalidDataException($"{_part.Name}: стекло {tile.Material} без записи в паспорте Glasses");
            mat.WithPBRMetallicRoughness(new Vector4(g.R, g.G, g.B, g.Alpha), null, null, g.Metallic, g.Roughness)
               .WithDoubleSide(true);
            if (g.Alpha < 1f)
            {
                mat.Alpha = AlphaMode.BLEND;
                info.AlphaMode = "BLEND";
            }
            info.Maps["glass"] = string.Create(CultureInfo.InvariantCulture,
                $"паспорт Glasses: alpha={g.Alpha:0.###} rough={g.Roughness:0.###} metal={g.Metallic:0.###} (без текстур)");
            if (g.Ior != null)
            {
                result.Notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{info.Name}: refraction {g.Ior:0.###} паспорта в glTF не записан (без KHR_materials_ior/transmission, как S1a-1)"));
            }
            mat.Extras = MaterialExtras(tile, info);
            return mat;
        }

        AgrTextureInfo? Map(string kind)
        {
            if (!tile.MapFiles.TryGetValue(kind, out var file) || file == null)
            {
                return null;
            }
            if (!tile.MapPaths.TryGetValue(kind, out var rel) || !textures.TryGetValue(TextureKey(rel), out var t))
            {
                throw new InvalidDataException($"{_part.Name}: нет пути картинки {kind} ({file}) тайла {tile.Material}|{tile.Tile}");
            }
            if (!t.Analyzed)
            {
                throw new InvalidOperationException(
                    $"{_part.Name}: текстура {t.File} без статистики — для записи glTF читайте часть с AnalyzeTextures = true");
            }
            return t;
        }

        var d = Map("Diffuse");
        var e = Map("ERM");
        var n = Map("Normal");

        Vector4 baseColor;
        Image? baseImage = null;
        // Однотонная по цвету Diffuse с непостоянной альфой остаётся картинкой: константа потеряла бы альфу (ревью C1b).
        if (d != null && (!d.Uniform || d.Alpha is { NonTrivial: true }))
        {
            baseColor = Vector4.One;
            baseImage = images.Use(d);
            info.Maps["Diffuse"] = "image " + d.File;
            if (d.Alpha is { NonTrivial: true } a)
            {
                info.AlphaMode = a.Mode == "MASK" ? "MASK" : "BLEND";
                info.Maps["Diffuse"] += " alpha=" + info.AlphaMode;
            }
        }
        else
        {
            var c = d?.Rgb255 ?? MissingDiffuse;
            baseColor = new Vector4(SrgbToLinear(c[0]), SrgbToLinear(c[1]), SrgbToLinear(c[2]), 1f);
            info.Maps["Diffuse"] = $"const sRGB {c[0]},{c[1]},{c[2]}" + (d == null ? " (нет файла)" : "");
        }

        float metallic, roughness;
        Image? ermImage = null;
        if (e != null && !e.Uniform)
        {
            metallic = 1f;
            roughness = 1f;
            ermImage = images.Use(e);
            info.Maps["ERM"] = "image " + e.File;
        }
        else
        {
            var c = e?.Rgb255 ?? MissingErm;
            roughness = (float)(c[1] / 255.0);
            metallic = (float)(c[2] / 255.0);
            info.Maps["ERM"] = string.Create(CultureInfo.InvariantCulture,
                $"const rough={c[1] / 255.0:0.####} metal={c[2] / 255.0:0.####}") + (e == null ? " (нет файла)" : "");
        }

        Image? normalImage = null;
        if (n != null && !n.Uniform)
        {
            normalImage = images.Use(n);
            info.Maps["Normal"] = "image " + n.File;
        }
        else
        {
            info.Maps["Normal"] = "flat" + (n == null ? " (нет файла)" : "");
        }

        mat.WithPBRMetallicRoughness(baseColor, null, null, metallic, roughness).WithDoubleSide(true);
        if (baseImage != null) SetTexture(mat, "BaseColor", baseImage);
        if (ermImage != null) SetTexture(mat, "MetallicRoughness", ermImage);
        if (normalImage != null) SetTexture(mat, "Normal", normalImage);
        if (info.AlphaMode == "MASK")
        {
            mat.Alpha = AlphaMode.MASK;
            mat.AlphaCutoff = 0.5f;
        }
        else if (info.AlphaMode == "BLEND")
        {
            mat.Alpha = AlphaMode.BLEND;
        }
        mat.Extras = MaterialExtras(tile, info);
        return mat;
    }

    /// <summary>extras.agr материала — как у S1a-1: исходный материал, тайл, класс, грани, описание каналов.</summary>
    static JsonNode MaterialExtras(AgrTileInfo tile, AgrGltfMaterial info)
    {
        var maps = new JsonObject();
        foreach (var (k, v) in info.Maps)
        {
            maps[k] = v;
        }
        return new JsonObject
        {
            ["agr"] = new JsonObject
            {
                ["source_material"] = tile.Material,
                ["udim_tile"] = tile.Tile,
                ["class"] = tile.Class,
                ["faces"] = tile.Faces,
                ["alpha_mode"] = info.AlphaMode,
                ["maps"] = maps,
            },
        };
    }

    static void SetTexture(Material mat, string channel, Image image)
    {
        var ch = mat.FindChannel(channel) ?? throw new InvalidOperationException($"у материала {mat.Name} нет канала {channel}");
        ch.SetTexture(0, image, null!, Wrap, Wrap, TextureMipMapFilter.LINEAR_MIPMAP_LINEAR, TextureInterpolationFilter.LINEAR);
    }

    static float SrgbToLinear(int c255)
    {
        double c = c255 / 255.0;
        return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
    }

    sealed record Glass(float R, float G, float B, float Alpha, float Roughness, float Metallic, double? Ior);

    /// <summary>Стекло по паспорту (как S1a-1): цвет sRGB, transparency — непрозрачность, roughness, metallicity.</summary>
    Glass? GlassOf(string material)
    {
        var glasses = _part.Passport?.Glasses;
        if (glasses == null)
        {
            return null;
        }
        // Узлы JsonObject паспорта разворачиваются лениво при первом обращении; часть может делиться между
        // параллельными писателями — чтение паспорта под замком словаря части (короткий, только на стекло).
        lock (glasses)
        {
            if (!glasses.TryGetValue(material, out var gv) && !glasses.TryGetValue(AgrRules.NormalizeMaterialName(material), out gv))
            {
                return null;
            }
            var color = gv["color_RGB"] as JsonObject;
            int Channel(string key) => (int)Math.Truncate(Num(color?[key], 204)); // как int() в S1a-1
            double alpha = Math.Clamp(Num(gv["transparency"], 1), 0, 1);
            double ior = Num(gv["refraction"], double.NaN);
            return new Glass(SrgbToLinear(Channel("Red")), SrgbToLinear(Channel("Green")), SrgbToLinear(Channel("Blue")),
                             (float)alpha, (float)Num(gv["roughness"], 0.5), (float)Num(gv["metallicity"], 0.0),
                             double.IsNaN(ior) || ior == 0 ? null : ior);
        }
    }

    static double Num(JsonNode? node, double fallback)
    {
        // значение из разобранного JSON отдаётся как double; созданное в коде — только своим типом
        if (node is JsonValue v)
        {
            if (v.TryGetValue(out double d)) return d;
            if (v.TryGetValue(out int i)) return i;
            if (v.TryGetValue(out long l)) return l;
            if (v.TryGetValue(out float f)) return f;
            if (v.TryGetValue(out decimal m)) return (double)m;
            if (v.TryGetValue(out string? s)
                && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        }
        return fallback;
    }

    /// <summary>Предел длины «папка .gltf + относительная ссылка» для картинки без копии (MAX_PATH 260 с запасом).</summary>
    const int MaxLinkedPath = 240;

    AgrGltfImage PlaceImage(AgrTextureInfo tex, string outDir)
    {
        string source = Path.GetFullPath(Path.Combine(_partDirectory, tex.RelativePath));
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"{_part.Name}: нет картинки {tex.RelativePath} в папке части", source);
        }
        if (_sameVolume(source, outDir))
        {
            // gltfpack склеивает папку .gltf и uri без нормализации «..»: путь длиннее MAX_PATH (260) Windows не откроет,
            // и gltfpack молча пропустит картинку («error reading source file»). Длинная ссылка — копия рядом с .gltf.
            string rel = Path.GetRelativePath(outDir, source);
            if (!Path.IsPathRooted(rel) && outDir.Length + 1 + rel.Length <= MaxLinkedPath)
            {
                return new AgrGltfImage
                {
                    File = tex.File, Source = source, Uri = EncodeRelativeUri(rel), Copied = false, Bytes = new FileInfo(source).Length,
                };
            }
        }
        // Копия — по пути внутри части (подпапки сохраняются): одноимённые картинки не затирают друг друга.
        string rel2 = TextureKey(tex.RelativePath);
        string target = Path.GetFullPath(Path.Combine(outDir, rel2));
        if (!target.StartsWith(outDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{_part.Name}: путь картинки {tex.RelativePath} выходит за папку вывода");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        long bytes = string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
            ? new FileInfo(source).Length
            : CopyStreamed(source, target);
        return new AgrGltfImage { File = tex.File, Source = source, Uri = EncodeRelativeUri(rel2), Copied = true, Bytes = bytes };
    }

    /// <summary>Ключ картинки: путь внутри части через «/», без «./».</summary>
    static string TextureKey(string relativePath) =>
        string.Join("/", relativePath.Split('\\', '/').Where(s => s.Length > 0 && s != "."));

    /// <summary>Потоковая копия с буфером 1 МБ: картинка целиком в память не читается.</summary>
    static long CopyStreamed(string source, string target)
    {
        const int Buffer = 1 << 20;
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, FileOptions.SequentialScan);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, Buffer);
        input.CopyTo(output, Buffer);
        return output.Length;
    }

    void BuildNodes(ModelRoot model, Node root, Dictionary<(string Material, int Tile), Material> materials, AgrGltfResult result)
    {
        var order = new List<string>();
        var byNode = new Dictionary<string, List<AgrMesh>>(StringComparer.Ordinal);
        foreach (var m in _part.Meshes)
        {
            if (!byNode.TryGetValue(m.Node, out var list))
            {
                byNode[m.Node] = list = new List<AgrMesh>();
                order.Add(m.Node);
            }
            list.Add(m);
        }
        result.Nodes = 1;
        foreach (var nodeName in order)
        {
            var prims = new SortedDictionary<(string Material, int Tile), PrimitiveBuilder>(Comparer<(string, int)>.Create(
                (a, b) => { int c = string.CompareOrdinal(a.Item1, b.Item1); return c != 0 ? c : a.Item2.CompareTo(b.Item2); }));
            foreach (var m in byNode[nodeName])
            {
                var maps = new Dictionary<(string, int), int[]>();
                for (int t = 0; t < m.TriangleCount; t++)
                {
                    var key = (m.Material, m.FaceTiles[t]);
                    if (!prims.TryGetValue(key, out var pb))
                    {
                        prims[key] = pb = new PrimitiveBuilder();
                    }
                    if (!maps.TryGetValue(key, out var map))
                    {
                        maps[key] = map = new int[m.VertexCount];
                        Array.Fill(map, -1);
                    }
                    pb.AddTriangle(m, t, map);
                }
            }

            string name = nodeName == _part.Name ? nodeName + "_mesh" : nodeName;
            var mesh = model.CreateMesh(name);
            foreach (var (key, pb) in prims)
            {
                var prim = mesh.CreatePrimitive();
                prim.WithVertexAccessor("POSITION", pb.Positions, false);
                if (pb.HasNormals) prim.WithVertexAccessor("NORMAL", pb.Normals, false);
                prim.WithVertexAccessor("TEXCOORD_0", pb.Uvs, false);
                prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, pb.Indices);
                prim.WithMaterial(materials.TryGetValue(key, out var mat)
                    ? mat
                    : throw new InvalidDataException($"{_part.Name}: нет тайла {key.Material}|{key.Item2} в разборе части"));
                result.Primitives++;
                result.Triangles += pb.Indices.Count / 3;
                result.Vertices += pb.Positions.Count;
            }
            root.CreateNode(name).WithMesh(mesh);
            result.Nodes++;
            result.Meshes++;
        }
    }

    /// <summary>Примитив одной пары (материал, тайл) в узле: вершины перенумерованы, UV сдвинуты в клетку тайла.</summary>
    sealed class PrimitiveBuilder
    {
        public readonly List<Vector3> Positions = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Vector2> Uvs = new();
        public readonly List<int> Indices = new();
        public bool HasNormals = true;

        // Сдвиг UV: клетка центроида грани (как B1/S1a-1). Он один на тайл, пока u и v тайла в 0..9; иначе вершина
        // с другим сдвигом получает отдельную копию.
        bool _hasShift;
        int _tu, _tv;
        Dictionary<(AgrMesh, int, int, int), int>? _other;

        public void AddTriangle(AgrMesh m, int t, int[] map)
        {
            int tu = 0, tv = 0;
            if (m.Uvs != null)
            {
                int i0 = m.Indices[t * 3], i1 = m.Indices[t * 3 + 1], i2 = m.Indices[t * 3 + 2];
                double cu = ((double)m.Uvs[i0 * 2] + m.Uvs[i1 * 2] + m.Uvs[i2 * 2]) / 3.0;
                double cv = ((double)m.Uvs[i0 * 2 + 1] + m.Uvs[i1 * 2 + 1] + m.Uvs[i2 * 2 + 1]) / 3.0;
                tu = (int)Math.Floor(cu);
                tv = (int)Math.Floor(cv);
            }
            if (!_hasShift)
            {
                _hasShift = true;
                _tu = tu;
                _tv = tv;
            }
            bool canonical = tu == _tu && tv == _tv;
            for (int c = 0; c < 3; c++)
            {
                int v = m.Indices[t * 3 + c];
                int ni;
                if (canonical)
                {
                    ni = map[v];
                    if (ni < 0) map[v] = ni = AddVertex(m, v, tu, tv);
                }
                else
                {
                    _other ??= new Dictionary<(AgrMesh, int, int, int), int>();
                    if (!_other.TryGetValue((m, v, tu, tv), out ni)) _other[(m, v, tu, tv)] = ni = AddVertex(m, v, tu, tv);
                }
                Indices.Add(ni);
            }
        }

        int AddVertex(AgrMesh m, int v, int tu, int tv)
        {
            float[] p = m.Positions;
            Positions.Add(new Vector3(p[v * 3], p[v * 3 + 2], -p[v * 3 + 1]));
            if (m.Normals is { } n)
            {
                Normals.Add(new Vector3(n[v * 3], n[v * 3 + 2], -n[v * 3 + 1]));
            }
            else
            {
                HasNormals = false;
            }
            if (m.Uvs is { } uv)
            {
                Uvs.Add(new Vector2((float)(uv[v * 2] - (double)tu), (float)(1.0 - (uv[v * 2 + 1] - (double)tv))));
            }
            else
            {
                Uvs.Add(new Vector2(0f, 1f));
            }
            return Positions.Count - 1;
        }
    }

    /// <summary>Картинки модели: одна на файл текстуры, содержимое — заглушка, имя записи — метка для колбэка.</summary>
    sealed class ImageSet
    {
        readonly ModelRoot _model;
        readonly MemoryImage _placeholder;
        readonly Dictionary<string, Image> _byFile = new(StringComparer.OrdinalIgnoreCase);

        public ImageSet(ModelRoot model, byte[] placeholderPng)
        {
            _model = model;
            _placeholder = new MemoryImage(placeholderPng);
        }

        public List<(string Token, AgrTextureInfo Texture)> Entries { get; } = new();

        public Image Use(AgrTextureInfo tex)
        {
            string key = TextureKey(tex.RelativePath);
            if (_byFile.TryGetValue(key, out var img))
            {
                return img;
            }
            img = _model.CreateImage(key);
            img.Content = _placeholder;
            string token = string.Create(CultureInfo.InvariantCulture, $"agr-image-{Entries.Count:0000}.png");
            img.AlternateWriteFileName = token;
            _byFile[key] = img;
            Entries.Add((token, tex));
            return img;
        }
    }
}
