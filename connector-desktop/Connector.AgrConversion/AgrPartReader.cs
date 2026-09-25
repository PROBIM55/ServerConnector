using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Connector.AgrConversion;

public sealed class AgrReadOptions
{
    /// <summary>Считать статистику текстур (WIC). Без неё — только геометрия, тайлы и паспорт.</summary>
    public bool AnalyzeTextures { get; init; } = true;

    /// <summary>
    /// Прогресс статистики текстур: (готово, всего) — перед каждой картинкой и в конце. Вызывается в потоке
    /// <see cref="AgrPartReader.Read"/> (служба конвертера C2a показывает этап «текстуры»).
    /// </summary>
    public Action<int, int>? TextureProgress { get; init; }

    /// <summary>Отмена между картинками статистики текстур.</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Разбор одной части АГР из папки: FBX (assimp) → сетки в осях АГР, разбиение по UDIM-тайлам по центроиду UV,
/// коллизии UCX_ и свет — в «отброшено», паспорт из geojson, текстуры через WIC, классы тайлов как в B1.
/// <para>
/// Потокобезопасность: <see cref="Read"/> можно вызывать на одном экземпляре из нескольких потоков — изменяемого
/// состояния у читателя нет (параметры неизменяемы, анализатор текстур без состояния, импорт FBX сериализован внутри
/// <see cref="AssimpFbxReader"/>). Возвращаемый <see cref="AgrPart"/> не потокобезопасен.
/// </para>
/// </summary>
public sealed class AgrPartReader
{
    static readonly Regex MaterialSetNo = new(@"_(\d+)$", RegexOptions.CultureInvariant);
    static readonly string[] Kinds = { "Diffuse", "ERM", "Normal" };

    // Параметры FBX пакета АВТОМАГИСТРАЛЬ (Blender 4.3, Z вверх, метры) — при других осях перевод S2 не проверен.
    static readonly (string Key, string Value)[] ExpectedFbxSettings =
    {
        ("UpAxis", "2"), ("UpAxisSign", "1"), ("FrontAxis", "1"), ("FrontAxisSign", "-1"),
        ("CoordAxis", "0"), ("CoordAxisSign", "1"), ("UnitScaleFactor", "100"),
    };
    static readonly double[] ExpectedRoot = { 1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1 };

    readonly AgrReadOptions _options;
    readonly AgrTextureAnalyzer _textures = new();

    public AgrPartReader(AgrReadOptions? options = null)
    {
        _options = options ?? new AgrReadOptions();
    }

    public AgrPart Read(string partDirectory, string? source = null)
    {
        var total = Stopwatch.StartNew();
        string root = Path.GetFullPath(partDirectory);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => (Full: p, Rel: Path.GetRelativePath(root, p), Name: Path.GetFileName(p)))
            .OrderBy(f => f.Rel, StringComparer.Ordinal)
            .ToList();
        var mainFbx = files.Where(f => AgrPackageReader.IsMainFbx(f.Name)).ToList();
        if (mainFbx.Count != 1)
        {
            throw new InvalidDataException(mainFbx.Count == 0
                ? $"{root}: нет главного SM_*.fbx"
                : $"{root}: несколько главных FBX: {string.Join(", ", mainFbx.Select(f => f.Rel))}");
        }
        string partName = Path.GetFileNameWithoutExtension(mainFbx[0].Name);

        var sw = Stopwatch.StartNew();
        FbxScene scene = AssimpFbxReader.Read(mainFbx[0].Full);
        double tImport = sw.Elapsed.TotalSeconds;

        var part = new AgrPart
        {
            Name = partName,
            FbxFile = mainFbx[0].Rel,
            Source = source ?? root,
            AssimpVersion = scene.AssimpVersion,
            ImportOptions = AssimpFbxReader.ImportDescription,
            FbxSettings = scene.Meta,
            RootTransform = scene.RootTransform,
        };
        CheckAxes(part, scene);

        var geojson = new List<string>();
        var textureFiles = new List<(string Full, string Rel, string Name)>();
        foreach (var f in files)
        {
            if (f.Full == mainFbx[0].Full) continue;
            if (AgrPackageReader.IsLightFbx(f.Name))
            {
                part.Dropped.LightFbx.Add(f.Rel);
            }
            else if (f.Name.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase))
            {
                geojson.Add(f.Full);
            }
            else if (AgrTextureAnalyzer.ParseName(f.Name) != null)
            {
                textureFiles.Add(f);
            }
            else
            {
                part.Dropped.IgnoredFiles.Add(f.Rel);
                part.Warnings.Add($"файл {f.Rel} не относится к части АГР — пропущен");
            }
        }

        BuildMeshes(part, scene);
        scene = null!;

        if (geojson.Count == 0)
        {
            part.Warnings.Add("нет geojson: паспорт и точка размещения неизвестны");
        }
        else
        {
            part.Passport = AgrGeoJsonReader.Read(partName, geojson);
            if (part.Passport.Point is { Length: >= 2 } pt)
            {
                part.Placement = new AgrPlacement { E = pt[0], N = pt[1], DZ = AgrGeoJsonReader.HRelief(part.Passport) };
            }
            else
            {
                part.Warnings.Add("в geojson нет точки (Point) — размещение неизвестно");
            }
        }

        sw.Restart();
        BuildTilesAndTextures(part, textureFiles);
        double tTex = sw.Elapsed.TotalSeconds;

        part.TimingsS["import"] = Math.Round(tImport, 3);
        part.TimingsS["textures"] = Math.Round(tTex, 3);
        part.TimingsS["total"] = Math.Round(total.Elapsed.TotalSeconds, 3);
        return part;
    }

    static void CheckAxes(AgrPart part, FbxScene scene)
    {
        var diff = new List<string>();
        foreach (var (key, value) in ExpectedFbxSettings)
        {
            scene.Meta.TryGetValue(key, out var v);
            if (v == null || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                || Math.Abs(d - double.Parse(value, CultureInfo.InvariantCulture)) > 1e-9)
            {
                diff.Add($"{key}={v ?? "нет"} (ожидалось {value})");
            }
        }
        for (int i = 0; i < 16 && scene.RootTransform.Length == 16; i++)
        {
            if (Math.Abs(scene.RootTransform[i] - ExpectedRoot[i]) > 1e-6)
            {
                diff.Add("корневая матрица assimp " + string.Join(",", scene.RootTransform.Select(x => x.ToString("0.######", CultureInfo.InvariantCulture))));
                break;
            }
        }
        if (diff.Count > 0)
        {
            part.Warnings.Add("оси/единицы FBX не как в пакете АВТОМАГИСТРАЛЬ, перевод осей не проверен: " + string.Join("; ", diff));
        }
    }

    static void BuildMeshes(AgrPart part, FbxScene scene)
    {
        var collNodes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        double overflow = 0;
        foreach (var m in scene.Meshes)
        {
            part.Dropped.NonTriangleFaces += m.NonTriangleFaces;
            if (m.Collision)
            {
                string pre = m.Node.Length >= 4 ? m.Node[..4] : m.Node;
                if (!part.Dropped.Collision.TryGetValue(pre, out var st))
                {
                    part.Dropped.Collision[pre] = st = new AgrCollisionStat();
                    collNodes[pre] = new HashSet<string>(StringComparer.Ordinal);
                }
                collNodes[pre].Add(m.Node);
                st.Triangles += m.TriangleCount;
                st.VerticesAssimp += m.VertexCount;
                continue;
            }
            int nt = m.TriangleCount;
            var faceTiles = new int[nt];
            if (m.Uvs == null)
            {
                Array.Fill(faceTiles, 1001);
                part.Warnings.Add($"сетка {m.Node} ({m.Material}) без UV — грани отнесены к тайлу 1001");
            }
            else
            {
                for (int t = 0; t < nt; t++)
                {
                    int i0 = m.Indices[t * 3], i1 = m.Indices[t * 3 + 1], i2 = m.Indices[t * 3 + 2];
                    faceTiles[t] = AgrRules.TileOfFace(m.Uvs, i0, i1, i2);
                    // грань должна лежать в клетке UV своего центроида целиком (straddle — как в B1/S2)
                    double cu = ((double)m.Uvs[i0 * 2] + m.Uvs[i1 * 2] + m.Uvs[i2 * 2]) / 3.0;
                    double cv = ((double)m.Uvs[i0 * 2 + 1] + m.Uvs[i1 * 2 + 1] + m.Uvs[i2 * 2 + 1]) / 3.0;
                    double tu = Math.Floor(cu), tv = Math.Floor(cv);
                    double over = Math.Max(Outside(m.Uvs, i0, tu, tv), Math.Max(Outside(m.Uvs, i1, tu, tv), Outside(m.Uvs, i2, tu, tv)));
                    if (over > 1e-4)
                    {
                        part.StraddleFaces++;
                        overflow = Math.Max(overflow, over);
                    }
                }
            }
            for (int v = 0; v < m.VertexCount; v++)
            {
                part.Bounds.Add(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
            }
            part.Meshes.Add(new AgrMesh
            {
                Node = m.Node,
                Material = m.Material,
                Positions = m.Positions,
                Normals = m.Normals,
                Uvs = m.Uvs,
                Indices = m.Indices,
                FaceTiles = faceTiles,
            });
        }
        foreach (var kv in collNodes)
        {
            part.Dropped.Collision[kv.Key].Objects = kv.Value.Count;
        }
        part.StraddleMaxOverflow = Math.Round(overflow, 6);
        if (part.StraddleFaces > 0)
        {
            part.Warnings.Add($"граней через границу UDIM-тайла: {part.StraddleFaces} (выход до {overflow:0.####} UV)");
        }
        if (part.Dropped.NonTriangleFaces > 0)
        {
            part.Warnings.Add($"граней не из трёх вершин (точки/линии): {part.Dropped.NonTriangleFaces} — пропущены");
        }
        if (part.Meshes.Count == 0)
        {
            part.Warnings.Add("в FBX нет сеток модели (только коллизии или пусто)");
        }
    }

    /// <summary>На сколько вершина вышла за клетку UV [tu, tu+1]×[tv, tv+1] (0 — внутри).</summary>
    static double Outside(float[] uv, int i, double tu, double tv)
    {
        double lu = uv[i * 2] - tu, lv = uv[i * 2 + 1] - tv;
        return Math.Max(Math.Max(-lu, lu - 1), Math.Max(Math.Max(-lv, lv - 1), 0));
    }

    void BuildTilesAndTextures(AgrPart part, List<(string Full, string Rel, string Name)> textureFiles)
    {
        // грани по (материал, тайл)
        var faces = new SortedDictionary<(string Mat, int Tile), int>(Comparer<(string, int)>.Create(
            (a, b) => { int c = string.CompareOrdinal(a.Item1, b.Item1); return c != 0 ? c : a.Item2.CompareTo(b.Item2); }));
        foreach (var m in part.Meshes)
        {
            foreach (int tile in m.FaceTiles)
            {
                faces.TryGetValue((m.Material, tile), out int c);
                faces[(m.Material, tile)] = c + 1;
            }
        }

        // текстуры: имя → запись (статистика — ниже, по одной картинке)
        var texByKey = new Dictionary<(string Stem, string Set, string Kind, int Tile), AgrTextureInfo>();
        var sets = new SortedSet<(string Stem, string Set)>();
        foreach (var f in textureFiles)
        {
            var n = AgrTextureAnalyzer.ParseName(f.Name)!;
            var info = new AgrTextureInfo
            {
                File = f.Name, RelativePath = f.Rel, Stem = n.Stem, Kind = n.Kind, KindRaw = n.KindRaw, Set = n.Set,
                Tile = n.Tile, Bytes = new FileInfo(f.Full).Length,
            };
            if (!texByKey.TryAdd((n.Stem, n.Set, n.Kind, n.Tile), info))
            {
                part.Warnings.Add($"две карты на один тайл: {f.Rel} — вторая пропущена");
                part.Dropped.IgnoredFiles.Add(f.Rel);
                continue;
            }
            if (Array.IndexOf(Kinds, n.Kind) < 0)
            {
                part.Warnings.Add($"карта {f.Name}: вид {n.KindRaw} не из Diffuse/ERM/Normal — не используется");
            }
            sets.Add((n.Stem, n.Set));
            part.Textures.Add(info);
        }

        // стекло — по паспорту Glasses (точное имя или без суффикса копии Blender)
        var glass = new HashSet<string>(StringComparer.Ordinal);
        var glassNames = part.Passport?.Glasses.Keys.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var usedMaterials = faces.Keys.Select(k => k.Mat).Distinct().ToList();
        foreach (var mat in usedMaterials)
        {
            if (glassNames.Contains(mat) || glassNames.Contains(AgrRules.NormalizeMaterialName(mat)))
            {
                glass.Add(mat);
            }
            else if (mat.Contains("glass", StringComparison.OrdinalIgnoreCase))
            {
                part.Warnings.Add($"материал {mat}: в имени Glass, записи в паспорте Glasses нет — по набору текстур");
            }
        }
        foreach (var g in glassNames)
        {
            if (!usedMaterials.Any(m => m == g || AgrRules.NormalizeMaterialName(m) == g))
            {
                part.Warnings.Add($"в паспорте Glasses материал {g}, которого нет среди граней модели");
            }
        }

        // материал → набор текстур (правило B1: номер набора в конце имени + stem в имени материала)
        var matSet = new Dictionary<string, (string Stem, string Set)?>(StringComparer.Ordinal);
        foreach (var mat in usedMaterials)
        {
            if (glass.Contains(mat))
            {
                matSet[mat] = null;
                continue;
            }
            var mm = MaterialSetNo.Match(AgrRules.NormalizeMaterialName(mat));
            var cand = sets.Where(s => mm.Success && s.Set == mm.Groups[1].Value && mat.Contains(s.Stem, StringComparison.Ordinal)).ToList();
            if (cand.Count != 1)
            {
                var byNo = sets.Where(s => mm.Success && s.Set == mm.Groups[1].Value).ToList();
                if (byNo.Count == 1)
                {
                    cand = byNo;
                    part.Warnings.Add($"материал {mat}: набор найден только по номеру: T_{cand[0].Stem}_*_{cand[0].Set}");
                }
                else if (sets.Count == 1)
                {
                    cand = sets.ToList();
                    part.Warnings.Add($"материал {mat}: единственный набор T_{cand[0].Stem}_*_{cand[0].Set} взят без совпадения имени");
                }
                else
                {
                    cand = new List<(string, string)>();
                    part.Warnings.Add($"материал {mat}: набор текстур не найден");
                }
            }
            matSet[mat] = cand.Count == 1 ? cand[0] : null;
        }

        // тайлы и использование карт
        foreach (var ((mat, tile), n) in faces)
        {
            var ti = new AgrTileInfo { Material = mat, Tile = tile, Faces = n };
            if (glass.Contains(mat))
            {
                ti.Set = "паспорт Glasses";
            }
            else if (matSet[mat] is { } set)
            {
                ti.Set = $"T_{set.Stem}_*_{set.Set}";
                foreach (var kind in Kinds)
                {
                    if (texByKey.TryGetValue((set.Stem, set.Set, kind, tile), out var t))
                    {
                        t.UsedFaces += n;
                        ti.MapFiles[kind] = t.File;
                        ti.MapPaths[kind] = t.RelativePath;
                    }
                    else
                    {
                        ti.MapFiles[kind] = null;
                        part.Warnings.Add($"нет карты {kind} для {mat} тайл {tile}");
                    }
                }
            }
            if (tile < 1001 || tile > 1100)
            {
                part.Warnings.Add($"тайл {tile} вне UDIM 1001–1100 ({mat}, граней {n})");
            }
            part.Tiles.Add(ti);
        }

        // статистика картинок — по одной; буфер пикселей у анализатора на вызов (ArrayPool), общего состояния нет
        if (_options.AnalyzeTextures)
        {
            var fullOf = textureFiles.ToDictionary(f => f.Rel, f => f.Full, StringComparer.Ordinal);
            int done = 0;
            foreach (var t in part.Textures)
            {
                _options.CancellationToken.ThrowIfCancellationRequested();
                _options.TextureProgress?.Invoke(done++, part.Textures.Count);
                _textures.Fill(fullOf[t.RelativePath], t);
            }
            _options.TextureProgress?.Invoke(done, part.Textures.Count);
        }

        var byFile = part.Textures.ToDictionary(t => t.File, StringComparer.Ordinal);
        foreach (var t in part.Textures)
        {
            t.Role = t.UsedFaces == 0 ? "unused" : !t.Analyzed ? "image" : t.Uniform ? "const" : "image";
            if (t.UsedFaces == 0) part.Dropped.UnusedTextures.Add(t.File);
            if (t.Analyzed && t.Kind == "ERM" && t.UsedFaces > 0 && t.ErmRed != null)
            {
                part.Dropped.Emissive.Add(new AgrEmissiveEntry
                {
                    File = t.File, Tile = t.Tile, UsedFaces = t.UsedFaces, Uniform = t.Uniform, Red = t.ErmRed,
                });
                if (t.ErmRed.CarriesEmissive)
                {
                    part.Warnings.Add($"ERM {t.File}: в R есть emissive (доля R>0,05 {t.ErmRed.FracAbove005:P2}, " +
                                      $"среднее {t.ErmRed.Mean255:0.#}/255) — не переносится");
                }
            }
            if (t.Analyzed && t.BitDepth is int bd && bd != 8)
            {
                part.Warnings.Add($"битность {bd} у {t.File}");
            }
            if (t.Analyzed && t.Alpha is { NonTrivial: true } a && t.Kind == "Diffuse" && t.UsedFaces > 0)
            {
                part.Warnings.Add($"альфа в {t.File}: средняя {a.Mean:0.###}, минимум {a.Min255}, промежуточных {a.FracMid:P1} → {a.Mode}");
            }
        }

        foreach (var ti in part.Tiles)
        {
            if (ti.Set == "паспорт Glasses")
            {
                ti.Class = "glass";
                continue;
            }
            bool real = false;
            foreach (var (kind, file) in ti.MapFiles)
            {
                if (file == null)
                {
                    ti.Maps[kind] = null;
                    continue;
                }
                var t = byFile[file];
                if (!t.Analyzed)
                {
                    ti.Maps[kind] = "image";
                    real = true;
                }
                else if (t.Uniform)
                {
                    ti.Maps[kind] = $"stub rgb[{t.Rgb255[0]}, {t.Rgb255[1]}, {t.Rgb255[2]}]";
                }
                else
                {
                    ti.Maps[kind] = $"real {t.W}x{t.H}";
                    real = true;
                }
            }
            ti.Class = real ? "real" : "stub";
        }
    }
}
