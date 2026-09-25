using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Connector.AgrConversion;

/// <summary>Уровень детализации: предел текстур и упрощение (доля треугольников и предел ошибки в метрах).</summary>
public sealed record AgrLevelSpec(string Name, int TextureLimit, double SimplifyRatio = 1, double ErrorM = 0,
                                  bool Permissive = false, bool LockBorder = false)
{
    public bool Simplifies => SimplifyRatio < 1;
}

public sealed class AgrConvertOptions
{
    /// <summary>Путь к gltfpack.exe (1.2; в MSI его кладёт C1d).</summary>
    public required string GltfpackPath { get; init; }

    /// <summary>Папка временных файлов (распаковка, промежуточный glTF, уровни); null — системная TEMP.</summary>
    public string? WorkRoot { get; init; }

    /// <summary>
    /// (н) Сколько раз собрать уровень с уменьшением -se, пока замер выше предела в метрах (допуска нет: предел строгий).
    /// Если и последняя сборка выше предела — уровень берёт геометрию следующего, более точного уровня (<c>simplify.fallback</c>).
    /// </summary>
    public int MaxErrorAttempts { get; init; } = 4;

    /// <summary>
    /// (н) «Малые сетки не упрощать»: часть, у которой меньше треугольников, идёт на всех уровнях полной геометрией
    /// (упрощение сэкономит килобайты, а ошибку даст). Флаги gltfpack 1.2 общие на файл — порог по части, не по сетке.
    /// </summary>
    public long MinTrianglesToSimplify { get; init; } = 5_000;

    /// <summary>
    /// -tj gltfpack — постоянный, не по числу ядер: байты KTX2 зависят от числа потоков кодирования (C1c-1-fix: 004 при
    /// -tj 1 и -tj 8 дал разные GLB всех уровней, при двух -tj 8 — одинаковые). Иначе пакет зависел бы от машины.
    /// </summary>
    public const int DefaultTextureThreads = 8;

    public int TextureThreads { get; init; } = DefaultTextureThreads;

    /// <summary>
    /// Уровни L0 → L2 (флаги эталона S1a-2/S1a-2b, требование (н)): предел ошибки упрощения — в метрах на уровень,
    /// в gltfpack уходит как <c>-se = метры / габарит части</c> (не больше <see cref="MaxRelativeError"/>).
    /// </summary>
    public IReadOnlyList<AgrLevelSpec> Levels { get; init; } = DefaultLevels;

    public static readonly IReadOnlyList<AgrLevelSpec> DefaultLevels = new[]
    {
        new AgrLevelSpec("L0", 256, SimplifyRatio: 0.1, ErrorM: 0.5, Permissive: true, LockBorder: true),
        new AgrLevelSpec("L1", 1024, SimplifyRatio: 0.5, ErrorM: 0.15, LockBorder: true),
        new AgrLevelSpec("L2", 2048),
    };

    /// <summary>
    /// Требование (и): 16 бит позиций дают ≤ 1 см в 3D, пока габарит ≤ 0,01 · 2 · 65535 / √3 = 756,7 м.
    /// Часть крупнее — без квантования (<c>-noq</c>, float32), как Ground в S1a-2b (требование (е)).
    /// </summary>
    public double QuantizationLimitM { get; init; } = QuantizationLimit(16, 0.01);

    public double MaxRelativeError { get; init; } = 0.01;

    /// <summary>Случайные точки по площади уровня для обратной ошибки (требование (о)): на треугольник, с пределами.</summary>
    public double SamplesPerTriangle { get; init; } = 8;
    public int MinAreaSamples { get; init; } = 200_000;
    public int MaxAreaSamples { get; init; } = 1_000_000;
    public int Seed { get; init; } = 20260925;

    /// <summary>Габарит, до которого квантование <paramref name="bits"/> бит даёт ошибку позиции ≤ <paramref name="errorM"/> в 3D.</summary>
    public static double QuantizationLimit(int bits, double errorM) => Math.Round(errorM * 2 * ((1 << bits) - 1) / Math.Sqrt(3), 1);
}

/// <summary>Этапы конвертации части для экрана: пять точек строки (C2, Р11).</summary>
public enum AgrConvertPhase
{
    /// <summary>Распаковка zip или открытие папки части.</summary>
    Read,
    /// <summary>Импорт FBX, сетки и тайлы.</summary>
    Geometry,
    /// <summary>Статистика картинок и запись промежуточного glTF с картинками.</summary>
    Textures,
    /// <summary>gltfpack и замер уровней L2 → L0.</summary>
    Levels,
    /// <summary>tileset.json, manifest.json и zip.</summary>
    Pack,
}

/// <summary>
/// Прогресс части. <see cref="Stage"/> — подпись шага; <see cref="Step"/>/<see cref="Steps"/> — счёт шагов (при
/// повторных сборках уровня шагов больше расчётного); <see cref="Phase"/> и <see cref="PhaseFraction"/> (0…1 внутри
/// этапа) — для полосы прогресса службы.
/// </summary>
public sealed record AgrConvertProgress(string Stage, int Step, int Steps)
{
    public AgrConvertPhase Phase { get; init; }
    public double PhaseFraction { get; init; }
}

/// <summary>(н) Уровень взял геометрию более точного уровня: после <see cref="Attempts"/> сборок ошибка выше предела.</summary>
public sealed record AgrLevelFallback(string GeometryOf, int Attempts, double ErrorM, double? SeRelative);

/// <summary>Итог уровня: треугольники, байты, ошибки, текстуры, команда gltfpack. Время — только здесь, не в manifest.json.</summary>
public sealed class AgrLevelResult
{
    public required string Name { get; init; }
    public long Triangles { get; set; }

    /// <summary>Треугольники по JSON настоящего GLB (узлы × индексы) — независимо от декодирования двойника.</summary>
    public long TrianglesGlb { get; set; }
    public long Bytes { get; set; }
    public double GeometricError { get; set; }
    public double GeomM { get; set; }
    public double TexelM { get; set; }
    public AgrDistanceStats? Forward { get; set; }
    public AgrDistanceStats? Reverse { get; set; }
    public double ReverseVerticesOnlyMax { get; set; }
    public AgrDegenerateStats? Degenerate { get; set; }
    public int Images { get; set; }
    public int Ktx2Images { get; set; }
    public int Attempts { get; set; } = 1;

    /// <summary>Геометрия уровня: упрощение и предел (у своего уровня — его спецификация, при fallback — более точного).</summary>
    public AgrLevelSpec? Geometry { get; set; }
    public bool Simplified { get; set; }
    public double ErrScale { get; set; } = 1;
    public AgrLevelFallback? Fallback { get; set; }
    public string SimplifySkipped { get; set; } = "";

    /// <summary>Сколько текстурных материалов исходника не нашлись в GLB по имени (для них тексель — по пределу уровня).</summary>
    public int TexelPxByLimit { get; set; }
    public int MaxImagePx { get; set; }
    public int MeshoptViews { get; set; }
    public List<string> ExtensionsUsed { get; } = new();
    public bool TwinMatches { get; set; }
    public double? SeRelative { get; set; }
    public required List<string> Flags { get; init; }
    public string Command { get; set; } = "";
    public string TwinCommand { get; set; } = "";
    public double PackSeconds { get; set; }
    public double TwinSeconds { get; set; }
    public double MeasureSeconds { get; set; }
    public double[] BoxMinZup { get; set; } = Array.Empty<double>();
    public double[] BoxMaxZup { get; set; } = Array.Empty<double>();
}

public sealed class AgrConvertResult
{
    public required string PartName { get; init; }
    public required string ZipPath { get; init; }
    public long ZipBytes { get; set; }
    public List<AgrLevelResult> Levels { get; } = new();
    public double ExtentM { get; set; }
    public bool Quantized { get; set; }
    public long TrianglesRef { get; set; }

    /// <summary>Треугольники исходного glTF по декодированным буферам — второй, независимый от писателя счётчик «было».</summary>
    public long TrianglesRefDecoded { get; set; }
    public List<string> Warnings { get; } = new();
    public SortedDictionary<string, double> TimingsS { get; } = new(StringComparer.Ordinal);
    public double Seconds { get; set; }

    /// <summary>manifest.json пакета (то же, что в zip).</summary>
    public JsonObject? Manifest { get; set; }

    /// <summary>Отчёт чтения части (<see cref="AgrReport"/>, JSON): в zip не входит, его кладёт рядом служба конвертера.</summary>
    public string? PartReportJson { get; set; }
}

/// <summary>
/// Пакет на часть (решение Р9: сборки нет, каждая часть — отдельная модель): пакет АГР (zip или папка части) →
/// <c>&lt;часть&gt;.glb.zip</c> с <c>tileset.json</c> (3D Tiles 1.1, HLOD REPLACE L0 → L1 → L2, размещение Р6),
/// GLB уровней (<c>parts/&lt;часть&gt;/L*.glb</c>) и <c>manifest.json</c>.
/// <para>
/// Ошибки уровней (требования (к), (о), Р8) считаются по «двойнику» уровня: тот же gltfpack с теми же флагами
/// геометрии, но без сжатия и текстур (<c>-tr</c>) — позиции и индексы читаются без декодера meshopt. Совпадение
/// двойника с настоящим GLB (число индексов и вершин, min/max позиций по примитивам) проверяется и пишется в манифест.
/// </para>
/// Временная папка удаляется в <c>finally</c>. Прогресс — по этапам <see cref="AgrConvertPhase"/> с долей внутри этапа
/// (картинки текстур, уровни по весу предела текстур). Отмена — между шагами, между картинками и внутри gltfpack
/// (процесс снимается); недописанный <c>.glb.zip.part</c> удаляется.
/// </summary>
public static class AgrPartConverter
{
    public const string Generator = "Connector.AgrConversion C1c-1";
    public const string MeasureMethod =
        "c1c1-measure-2: fwd = уникальные вершины исходника → поверхность уровня; rev = вершины уровня и случайные " +
        "точки по площади его треугольников → поверхность исходника (ближайшая точка треугольника, дерево AABB, double); " +
        "texel = Σ(A·sqrt(A/A_uv) / px) / ΣA по примитивам с текстурой, px — сторона картинки материала в GLB уровня (≤ min(предел, 4096))";

    static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AgrConvertResult ConvertPart(string input, string outputDirectory, AgrConvertOptions options,
                                               IProgress<AgrConvertProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sw = Stopwatch.StartNew();
        var sources = AgrPackageReader.Discover(input);
        if (sources.Count != 1)
        {
            throw new InvalidDataException($"{input}: ожидалась одна часть, найдено {sources.Count}");
        }
        var source = sources[0];
        if (!File.Exists(options.GltfpackPath))
        {
            throw new FileNotFoundException($"нет gltfpack: {options.GltfpackPath}", options.GltfpackPath);
        }
        string outDir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outDir);
        string work = Path.Combine(Path.GetFullPath(options.WorkRoot ?? Path.GetTempPath()), "agr-conv-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(work);
        int steps = 4 + options.Levels.Count * 2;
        int step = 0;
        void Stage(string name, AgrConvertPhase phase, double fraction)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AgrConvertProgress(name, ++step, steps) { Phase = phase, PhaseFraction = Math.Clamp(fraction, 0, 1) });
        }
        // Доля этапа «уровни»: вес уровня — предел текстур (KTX2 — основное время gltfpack: у 001 L2 52 с, L1 20 с, L0 6 с).
        double levelWeightSum = options.Levels.Sum(l => (double)Math.Max(1, l.TextureLimit));
        double levelBase = 0, levelWeight = 0;
        try
        {
            var reader = new AgrPackageReader(work);
            Stage(source.Kind == AgrSourceKind.Zip ? "распаковка" : "чтение части", AgrConvertPhase.Read, 0);
            return reader.WithPartDirectory(source, partDir =>
            {
                Stage("чтение FBX", AgrConvertPhase.Geometry, 0);
                var t0 = sw.Elapsed.TotalSeconds;
                var readOptions = new AgrReadOptions
                {
                    CancellationToken = cancellationToken,
                    TextureProgress = (done, total) => progress?.Report(new AgrConvertProgress($"текстуры {done} из {total}", step, steps)
                    {
                        Phase = AgrConvertPhase.Textures, PhaseFraction = total == 0 ? 0.8 : 0.8 * done / total,
                    }),
                };
                var part = new AgrPartReader(readOptions).Read(partDir, source.Kind == AgrSourceKind.Zip ? "zip " + Path.GetFileName(source.Path) : source.Path);
                string stem = AgrGltfWriter.FileStem(part.Name);
                var result = new AgrConvertResult { PartName = part.Name, ZipPath = Path.Combine(outDir, stem + ".glb.zip") };
                result.TimingsS["read"] = Math.Round(sw.Elapsed.TotalSeconds - t0, 2);

                Stage("запись glTF", AgrConvertPhase.Textures, 0.8);
                t0 = sw.Elapsed.TotalSeconds;
                var gltf = new AgrGltfWriter(part, partDir).Write(Path.Combine(work, "gltf"));
                result.TimingsS["gltf"] = Math.Round(sw.Elapsed.TotalSeconds - t0, 2);
                result.TrianglesRef = gltf.Triangles;

                var src = AgrGlbGeometry.Load(gltf.GltfPath);
                var srcPrims = src.Primitives(withUv: true);
                var (smin, smax) = Bounds(srcPrims);
                result.ExtentM = Math.Max(smax[0] - smin[0], Math.Max(smax[1] - smin[1], smax[2] - smin[2]));
                result.Quantized = result.ExtentM <= options.QuantizationLimitM;
                result.TrianglesRefDecoded = srcPrims.Sum(p => (long)p.TriangleCount);
                var texturedMaterials = TexturedMaterials(src.Json);
                var materialNames = MaterialNames(src.Json);
                var srcTree = new AgrTriangleTree(srcPrims);
                double[] srcVerts = AgrMeshSampling.UniqueUsedVertices(srcPrims);

                string pkg = Path.Combine(work, "pkg");
                string levelDir = Path.Combine(pkg, "parts", stem);
                string twinDir = Path.Combine(work, "twin");
                Directory.CreateDirectory(levelDir);
                Directory.CreateDirectory(twinDir);
                var gp = new GltfpackInfo(options.GltfpackPath);
                bool smallPart = result.TrianglesRef < options.MinTrianglesToSimplify;

                // Уровень собирается и меряется: gltfpack уровня и двойника, затем ошибки по двойнику.
                AgrLevelResult Build(AgrLevelSpec spec, AgrLevelSpec geometry, double errScale, int attempt)
                {
                    var (flags, twinFlags, se) = LevelFlags(spec, result.Quantized, result.ExtentM * errScale, options, geometry);
                    var lr = new AgrLevelResult
                    {
                        Name = spec.Name, Flags = flags, SeRelative = se, Attempts = attempt, Geometry = geometry,
                        Simplified = geometry.Simplifies, ErrScale = errScale,
                    };
                    string glb = Path.Combine(levelDir, spec.Name + ".glb");
                    string twin = Path.Combine(twinDir, spec.Name + ".glb");
                    lr.PackSeconds = RunGltfpack(gp.Path, gltf.GltfPath, glb, flags, work, out string packLog, cancellationToken);
                    foreach (var wl in packLog.Split('\n').Select(x => x.Trim()).Where(x => x.StartsWith("Warning", StringComparison.OrdinalIgnoreCase)).Distinct())
                    {
                        result.Warnings.Add($"{spec.Name}: gltfpack: {wl}");
                    }
                    if (packLog.Contains("unable to encode image", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"{part.Name} {spec.Name}: gltfpack пропустил картинку — {Tail(packLog, 600)}");
                    }
                    lr.TwinSeconds = RunGltfpack(gp.Path, gltf.GltfPath, twin, twinFlags, work, out _, cancellationToken);
                    lr.Command = $"gltfpack -i {Path.GetFileName(gltf.GltfPath)} -o parts/{stem}/{spec.Name}.glb {string.Join(" ", flags)}";
                    lr.TwinCommand = $"gltfpack -i {Path.GetFileName(gltf.GltfPath)} -o twin/{spec.Name}.glb {string.Join(" ", twinFlags)}";

                    Stage("замер " + spec.Name, AgrConvertPhase.Levels, levelBase + 0.9 * levelWeight);
                    double t1 = sw.Elapsed.TotalSeconds;
                    Measure(lr, spec, glb, twin, srcTree, srcVerts, srcPrims, texturedMaterials, materialNames, options);
                    lr.MeasureSeconds = Math.Round(sw.Elapsed.TotalSeconds - t1, 2);
                    try { File.Delete(twin); } catch (IOException) { }
                    return lr;
                }

                // От точного к грубому: fallback уровня берёт готовую геометрию следующего уровня (L0 — флаги L1, L1 — полная).
                var levels = new AgrLevelResult[options.Levels.Count];
                var own = new AgrLevelResult[options.Levels.Count]; // последняя сборка уровня по его собственным флагам
                for (int li = options.Levels.Count - 1; li >= 0; li--)
                {
                    var spec = options.Levels[li];
                    levelWeight = Math.Max(1, spec.TextureLimit) / levelWeightSum;
                    Stage("gltfpack " + spec.Name, AgrConvertPhase.Levels, levelBase);
                    var geometry = spec.Simplifies && smallPart ? spec with { SimplifyRatio = 1, ErrorM = 0 } : spec;
                    double errScale = 1;
                    AgrLevelResult lr;
                    for (int attempt = 1; ; attempt++)
                    {
                        lr = Build(spec, geometry, errScale, attempt);
                        double geomErr = Math.Max(lr.Forward!.Max, lr.Reverse!.Max);
                        // (н) предел строгий: gltfpack считает ошибку в вершинах, а хорды срезают кривые — решает замер.
                        if (!geometry.Simplifies || spec.ErrorM <= 0 || lr.SeRelative is null || geomErr <= spec.ErrorM
                            || attempt >= options.MaxErrorAttempts) break;
                        errScale *= Math.Clamp(geomErr / spec.ErrorM, 1.25, 4.0);
                    }
                    if (spec.Simplifies && smallPart)
                    {
                        lr.SimplifySkipped = string.Create(CultureInfo.InvariantCulture,
                            $"часть {result.TrianglesRef} треугольников < порога {options.MinTrianglesToSimplify}: полная геометрия");
                    }
                    own[li] = lr;
                    double err = Math.Max(lr.Forward!.Max, lr.Reverse!.Max);
                    if (geometry.Simplifies && spec.ErrorM > 0 && err > spec.ErrorM && li + 1 < levels.Length)
                    {
                        // Флаги более точного уровня: его последняя собственная сборка, если она в нашем пределе
                        // (у 012 L1 = 0,1508 м при пределе L1 0,15 — годится для L0 с 0,5), иначе его итоговая геометрия.
                        var finer = levels[li + 1];
                        var finerOwn = own[li + 1];
                        bool useOwn = finerOwn.Simplified && Math.Max(finerOwn.Forward!.Max, finerOwn.Reverse!.Max) <= spec.ErrorM;
                        var src2 = useOwn ? finerOwn : finer;
                        var failed = new AgrLevelFallback(useOwn ? finer.Name : finer.Fallback?.GeometryOf ?? finer.Name, lr.Attempts, R(err, 5), lr.SeRelative);
                        lr = Build(spec, src2.Geometry!, src2.ErrScale, lr.Attempts + 1);
                        lr.Attempts = failed.Attempts;
                        lr.Fallback = failed;
                        err = Math.Max(lr.Forward!.Max, lr.Reverse!.Max);
                    }
                    if (spec.ErrorM > 0 && err > spec.ErrorM)
                    {
                        result.Warnings.Add(string.Create(CultureInfo.InvariantCulture,
                            $"{spec.Name}: ошибка {err:0.#####} м выше предела {spec.ErrorM} м после {lr.Attempts} сборок{(lr.Fallback != null ? " и геометрии " + lr.Fallback.GeometryOf : "")}"));
                    }
                    if (!lr.TwinMatches)
                    {
                        result.Warnings.Add($"{spec.Name}: двойник без сжатия не совпал с GLB по индексам/вершинам — ошибки уровня под вопросом");
                    }
                    levels[li] = lr;
                    levelBase += levelWeight;
                }
                result.Levels.AddRange(levels);

                Stage("tileset и manifest", AgrConvertPhase.Pack, 0);
                var tileset = BuildTileset(part, stem, result);
                var manifest = BuildManifest(part, stem, source, gltf, result, gp, options);
                result.Manifest = manifest;
                result.PartReportJson = AgrReport.ToJson(part);
                File.WriteAllText(Path.Combine(pkg, "tileset.json"), tileset.ToJsonString(JsonOut));
                File.WriteAllText(Path.Combine(pkg, "manifest.json"), manifest.ToJsonString(JsonOut));
                string tmpZip = result.ZipPath + ".part";
                try
                {
                    WriteZip(pkg, tmpZip);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(tmpZip, result.ZipPath, overwrite: true);
                }
                catch
                {
                    // Недописанный zip не остаётся в папке выхода ни при отмене, ни при ошибке.
                    try { File.Delete(tmpZip); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    throw;
                }
                result.ZipBytes = new FileInfo(result.ZipPath).Length;
                result.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                return result;
            });
        }
        finally
        {
            AgrPackageReader.DeleteExtraction(work, throwOnFailure: false);
        }
    }

    /// <summary>
    /// Флаги gltfpack уровня и его двойника без сжатия и текстур; -se — метры / габарит части. Текстуры — по
    /// <paramref name="spec"/>, упрощение — по <paramref name="geometry"/> (по умолчанию тот же уровень; при fallback —
    /// более точный уровень, у малой части — без упрощения).
    /// </summary>
    public static (List<string> Flags, List<string> Twin, double? Se) LevelFlags(AgrLevelSpec spec, bool quantized, double extentM,
                                                                                 AgrConvertOptions options, AgrLevelSpec? geometry = null)
    {
        var g = geometry ?? spec;
        var pos = quantized ? new List<string> { "-vp", "16", "-vt", "14" } : new List<string> { "-noq" };
        var simplify = new List<string>();
        double? se = null;
        if (g.Simplifies)
        {
            simplify.AddRange(new[] { "-si", F(g.SimplifyRatio) });
            if (g.ErrorM > 0 && extentM > 0)
            {
                se = Math.Min(g.ErrorM / extentM, options.MaxRelativeError);
                simplify.AddRange(new[] { "-se", se.Value.ToString("0.##########", CultureInfo.InvariantCulture) });
            }
            if (g.Permissive) simplify.Add("-sp");
            if (g.LockBorder) simplify.Add("-slb");
        }
        var flags = new List<string> { "-cc", "-ce", "ext", "-km", "-kn", "-ke", "-tc", "-tu", "normal,attrib",
                                       "-tj", options.TextureThreads.ToString(CultureInfo.InvariantCulture) };
        flags.AddRange(pos);
        flags.AddRange(new[] { "-tl", spec.TextureLimit.ToString(CultureInfo.InvariantCulture) });
        flags.AddRange(simplify);
        var twin = new List<string> { "-km", "-kn", "-ke", "-tr" };
        twin.AddRange(pos);
        twin.AddRange(simplify);
        return (flags, twin, se);
    }

    static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    sealed class GltfpackInfo
    {
        public GltfpackInfo(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
            using (var fs = File.OpenRead(Path))
            {
                Sha256 = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            }
            var psi = new ProcessStartInfo(Path, "-v") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Version = o.Trim();
        }

        public string Path { get; }
        public string Sha256 { get; }
        public string Version { get; }
    }

    /// <summary>Отмена снимает процесс gltfpack (со всем деревом) и бросает <see cref="OperationCanceledException"/>.</summary>
    static double RunGltfpack(string exe, string input, string output, List<string> flags, string work, out string log,
                              CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = work,
        };
        string tmp = Path.Combine(work, "gltfpack-temp");
        Directory.CreateDirectory(tmp);
        psi.Environment["TEMP"] = tmp;
        psi.Environment["TMP"] = tmp;
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(input);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(output);
        foreach (var f in flags) psi.ArgumentList.Add(f);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("gltfpack не запустился");
        Exception? killError = null;
        using var kill = cancellationToken.Register(() =>
        {
            // Идёт в потоке, вызвавшем Cancel (окно), — не бросать: уже вышел, нет прав, AggregateException по дереву.
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) { killError = ex; }
        });
        var err = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (cancellationToken.IsCancellationRequested)
        {
            throw killError is { } ke and not InvalidOperationException
                ? new OperationCanceledException($"gltfpack снят не сразу: {ke.Message}", ke, cancellationToken)
                : new OperationCanceledException(cancellationToken);
        }
        string all = stdout + err.Result;
        log = all;
        if (p.ExitCode != 0 || !File.Exists(output))
        {
            throw new InvalidOperationException($"gltfpack {Path.GetFileName(output)}: код {p.ExitCode}: {Tail(all, 800)}");
        }
        return Math.Round(sw.Elapsed.TotalSeconds, 2);
    }

    static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];

    static void Measure(AgrLevelResult lr, AgrLevelSpec spec, string glb, string twin, AgrTriangleTree srcTree, double[] srcVerts,
                        IReadOnlyList<AgrGeomPrimitive> srcPrims, HashSet<int> texturedMaterials, IReadOnlyList<string?> materialNames,
                        AgrConvertOptions options)
    {
        lr.Bytes = new FileInfo(glb).Length;
        var real = AgrGlbGeometry.Load(glb);
        foreach (var e in real.Json["extensionsUsed"]?.AsArray() ?? new JsonArray()) lr.ExtensionsUsed.Add(e!.GetValue<string>());
        lr.MeshoptViews = (real.Json["bufferViews"]?.AsArray() ?? new JsonArray())
            .Count(v => v?["extensions"]?["EXT_meshopt_compression"] != null);
        var images = real.Images();
        lr.Images = images.Count;
        lr.Ktx2Images = images.Count(i => i.Mime == "image/ktx2");
        lr.MaxImagePx = images.Count == 0 ? 0 : images.Max(i => Math.Max(i.Width, i.Height));
        lr.TrianglesGlb = GlbTriangles(real.Json);

        // Тексель — по стороне картинки материала в этом GLB (имена материалов сохраняет -km), не больше предела уровня.
        int limitPx = Math.Min(spec.TextureLimit, 4096);
        var pxByName = BaseColorPx(real.Json, images);
        int byLimit = 0;
        var pxOf = new Dictionary<int, int>();
        foreach (int m in texturedMaterials)
        {
            string? name = m < materialNames.Count ? materialNames[m] : null;
            if (name != null && pxByName.TryGetValue(name, out int px) && px > 0)
            {
                pxOf[m] = Math.Min(px, limitPx);
            }
            else
            {
                pxOf[m] = limitPx;
                byLimit++;
            }
        }
        lr.TexelPxByLimit = byLimit;
        double texel = AgrMeshSampling.Texel(srcPrims, m => m is int i && pxOf.TryGetValue(i, out int px) ? px : 0);

        var tw = AgrGlbGeometry.Load(twin);
        lr.TwinMatches = PrimitiveSignature(real.Json).SequenceEqual(PrimitiveSignature(tw.Json));
        MeasureGeometry(lr, spec, tw.Primitives(), srcTree, srcVerts, texel, options);
    }

    /// <summary>
    /// Ошибки уровня по его геометрии (требования (к), (о), Р8): прямая — вершины исходника → поверхность уровня;
    /// обратная — вершины уровня и (если уровень упрощён) случайные точки по площади → поверхность исходника;
    /// geometricError = max(обе, тексель), у последнего уровня — 0. Тексель — готовый, в метрах (<see cref="AgrMeshSampling.Texel"/>).
    /// </summary>
    public static void MeasureGeometry(AgrLevelResult lr, AgrLevelSpec spec, IReadOnlyList<AgrGeomPrimitive> prims, AgrTriangleTree srcTree,
                                       double[] srcVerts, double texelM, AgrConvertOptions options)
    {
        lr.Triangles = prims.Sum(p => (long)p.TriangleCount);
        lr.Degenerate = AgrMeshSampling.Degenerate(prims);
        var (mn, mx) = Bounds(prims);
        lr.BoxMinZup = new[] { mn[0], -mx[2], mn[1] };
        lr.BoxMaxZup = new[] { mx[0], -mn[2], mx[1] };

        var tree = new AgrTriangleTree(prims);
        lr.Forward = AgrDistanceStats.Of(tree.Distances(srcVerts));
        double[] verts = AgrMeshSampling.UniqueUsedVertices(prims);
        double[] vd = srcTree.Distances(verts);
        lr.ReverseVerticesOnlyMax = vd.Length == 0 ? 0 : vd.Max();
        double[] rev = vd;
        if (lr.Simplified)
        {
            int n = (int)Math.Clamp(lr.Triangles * options.SamplesPerTriangle, options.MinAreaSamples, options.MaxAreaSamples);
            double[] ad = srcTree.Distances(AgrMeshSampling.AreaSamples(prims, n, options.Seed));
            rev = vd.Concat(ad).ToArray();
        }
        lr.Reverse = AgrDistanceStats.Of(rev);
        lr.TexelM = Math.Round(texelM, 6);
        double geom = Math.Max(lr.Forward.Max, lr.Reverse.Max);
        lr.GeomM = Math.Round(geom, 5);
        // Р8: geometricError уровня — max(геометрия в обе стороны, тексель); у листа (последний уровень) — 0.
        lr.GeometricError = spec.Name == options.Levels[^1].Name ? 0 : Math.Round(Math.Max(geom, lr.TexelM), 4);
    }

    /// <summary>Треугольники по JSON GLB: для каждого узла с сеткой — индексы её примитивов / 3.</summary>
    static long GlbTriangles(JsonObject json)
    {
        var acc = json["accessors"]!.AsArray();
        var meshes = json["meshes"]?.AsArray() ?? new JsonArray();
        long n = 0;
        foreach (var node in json["nodes"]?.AsArray() ?? new JsonArray())
        {
            if (node?["mesh"] is not JsonNode mi) continue;
            foreach (var p in meshes[mi.GetValue<int>()]!["primitives"]!.AsArray())
            {
                int a = p!["indices"] is JsonNode ix ? ix.GetValue<int>() : p["attributes"]!["POSITION"]!.GetValue<int>();
                n += acc[a]!["count"]!.GetValue<long>() / 3;
            }
        }
        return n;
    }

    /// <summary>Имя материала → большая сторона картинки baseColor в GLB (KTX2 — через KHR_texture_basisu); у одноимённых — меньшая.</summary>
    static Dictionary<string, int> BaseColorPx(JsonObject json, List<(string Mime, int Width, int Height, int Bytes)> images)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var textures = json["textures"]?.AsArray() ?? new JsonArray();
        foreach (var m in json["materials"]?.AsArray() ?? new JsonArray())
        {
            string? name = m?["name"]?.GetValue<string>();
            if (name == null || m!["pbrMetallicRoughness"]?["baseColorTexture"]?["index"] is not JsonNode ti) continue;
            var tex = textures[ti.GetValue<int>()];
            var src = tex?["extensions"]?["KHR_texture_basisu"]?["source"] ?? tex?["source"];
            if (src == null) continue;
            var im = images[src.GetValue<int>()];
            int px = Math.Max(im.Width, im.Height);
            map[name] = map.TryGetValue(name, out int old) ? Math.Min(old, px) : px;
        }
        return map;
    }

    /// <summary>Подпись примитивов GLB по JSON: число индексов, вершин и min/max позиций — для сверки с двойником.</summary>
    static List<string> PrimitiveSignature(JsonObject json)
    {
        var acc = json["accessors"]!.AsArray();
        var list = new List<string>();
        foreach (var mesh in json["meshes"]?.AsArray() ?? new JsonArray())
        {
            foreach (var p in mesh!["primitives"]!.AsArray())
            {
                var pa = acc[p!["attributes"]!["POSITION"]!.GetValue<int>()]!;
                int ni = p["indices"] is JsonNode ix ? acc[ix.GetValue<int>()]!["count"]!.GetValue<int>() : -1;
                list.Add($"{ni}|{pa["count"]}|{pa["min"]?.ToJsonString()}|{pa["max"]?.ToJsonString()}");
            }
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    static List<string?> MaterialNames(JsonObject json) =>
        (json["materials"]?.AsArray() ?? new JsonArray()).Select(m => m?["name"]?.GetValue<string>()).ToList();

    static HashSet<int> TexturedMaterials(JsonObject json)
    {
        var set = new HashSet<int>();
        var mats = json["materials"]?.AsArray() ?? new JsonArray();
        for (int i = 0; i < mats.Count; i++)
        {
            if (mats[i]?["pbrMetallicRoughness"]?["baseColorTexture"] != null) set.Add(i);
        }
        return set;
    }

    static (double[] Min, double[] Max) Bounds(IEnumerable<AgrGeomPrimitive> prims)
    {
        var mn = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
        var mx = new[] { double.MinValue, double.MinValue, double.MinValue };
        foreach (var p in prims)
        {
            foreach (int v in p.Indices)
            {
                for (int a = 0; a < 3; a++)
                {
                    double x = p.Positions[v * 3 + a];
                    if (x < mn[a]) mn[a] = x;
                    if (x > mx[a]) mx[a] = x;
                }
            }
        }
        if (mn[0] > mx[0])
        {
            return (new double[3], new double[3]);
        }
        return (mn, mx);
    }

    static double R(double v, int d) => Math.Round(v, d, MidpointRounding.AwayFromZero);

    static JsonArray Box(double[] mn, double[] mx)
    {
        var c = new double[3];
        var h = new double[3];
        for (int i = 0; i < 3; i++)
        {
            c[i] = (mn[i] + mx[i]) / 2;
            h[i] = Math.Max((mx[i] - mn[i]) / 2, 0.0005);
        }
        return new JsonArray(R(c[0], 4), R(c[1], 4), R(c[2], 4), R(h[0], 4), 0, 0, 0, R(h[1], 4), 0, 0, 0, R(h[2], 4));
    }

    static double Radius(double[] mn, double[] mx) =>
        Math.Sqrt(Math.Pow(mx[0] - mn[0], 2) + Math.Pow(mx[1] - mn[1], 2) + Math.Pow(mx[2] - mn[2], 2)) / 2;

    public static string ShortName(string part)
    {
        int i = part.LastIndexOf('_');
        return i >= 0 && i < part.Length - 1 ? part[(i + 1)..] : part;
    }

    /// <summary>tileset.json части: корень — часть (перенос Р6, метаданные agrPart), под ним цепочка L0 → L1 → L2.</summary>
    public static JsonObject BuildTileset(AgrPart part, string stem, AgrConvertResult result)
    {
        JsonObject? child = null;
        double[]? cmin = null, cmax = null;
        for (int i = result.Levels.Count - 1; i >= 0; i--)
        {
            var lr = result.Levels[i];
            var bmin = lr.BoxMinZup.Select(v => v - 0.001).ToArray();
            var bmax = lr.BoxMaxZup.Select(v => v + 0.001).ToArray();
            var amin = cmin == null ? bmin : bmin.Zip(cmin, Math.Min).ToArray();
            var amax = cmax == null ? bmax : bmax.Zip(cmax, Math.Max).ToArray();
            var tile = new JsonObject
            {
                ["boundingVolume"] = new JsonObject { ["box"] = Box(amin, amax) },
                ["geometricError"] = lr.GeometricError,
                ["refine"] = "REPLACE",
                ["content"] = new JsonObject
                {
                    ["uri"] = $"parts/{AgrGltfWriter.EncodeRelativeUri(stem)}/{lr.Name}.glb",
                    ["boundingVolume"] = new JsonObject { ["box"] = Box(bmin, bmax) },
                },
                ["extras"] = new JsonObject { ["agr"] = new JsonObject { ["geom_m"] = lr.GeomM, ["texel_m"] = lr.TexelM } },
            };
            if (child != null) tile["children"] = new JsonArray(child);
            child = tile;
            cmin = amin;
            cmax = amax;
        }
        if (child == null || cmin == null || cmax == null)
        {
            throw new InvalidOperationException($"{part.Name}: нет уровней");
        }
        double radius = Radius(cmin, cmax);
        var pl = part.Placement;
        var root = new JsonObject();
        if (pl != null)
        {
            root["transform"] = new JsonArray(1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, pl.E, pl.N, pl.DZ ?? 0.0, 1.0);
        }
        else
        {
            result.Warnings.Add("нет точки geojson: часть без переноса (Р6 не применено)");
        }
        foreach (var kv in new JsonObject
        {
            ["boundingVolume"] = new JsonObject { ["box"] = Box(cmin, cmax) },
            ["geometricError"] = R(radius, 4),
            ["refine"] = "REPLACE",
            ["metadata"] = new JsonObject
            {
                ["class"] = "agrPart",
                ["properties"] = new JsonObject { ["part"] = part.Name, ["short"] = ShortName(part.Name), ["code"] = "agr:" + part.Name },
            },
            ["children"] = new JsonArray(child),
        }.ToList())
        {
            root[kv.Key] = kv.Value?.DeepClone();
        }
        if (pl is { DZ: null })
        {
            result.Warnings.Add("нет h_relief в паспорте: ΔZ = 0");
        }
        return new JsonObject
        {
            ["asset"] = new JsonObject
            {
                ["version"] = "1.1",
                ["tilesetVersion"] = "C1c-1 " + ShortName(part.Name),
                ["extras"] = new JsonObject { ["generator"] = Generator + ", gltfpack 1.2" },
            },
            ["schema"] = new JsonObject
            {
                ["id"] = "agr",
                ["classes"] = new JsonObject
                {
                    ["agrPart"] = new JsonObject
                    {
                        ["name"] = "Часть АГР",
                        ["properties"] = new JsonObject
                        {
                            ["part"] = new JsonObject { ["type"] = "STRING" },
                            ["short"] = new JsonObject { ["type"] = "STRING" },
                            ["code"] = new JsonObject { ["type"] = "STRING", ["description"] = "как applicationId в Speckle" },
                        },
                    },
                },
            },
            ["geometricError"] = R(2 * radius, 4),
            ["extras"] = new JsonObject
            {
                ["crs"] = "МСК-77, высоты Балтийские; локальная система без ECEF (как модели Speckle)",
                ["up"] = "Z; содержимое glTF Y-up поворачивается по спецификации 3D Tiles",
                ["placement"] = "transform части = перенос (E, N, ΔZ): X = x + E, Y = y + N, Z = z + ΔZ",
            },
            ["root"] = root,
        };
    }

    /// <summary>
    /// manifest.json: только то, что определяется входом и параметрами — без времени и числа потоков, чтобы два прогона одной
    /// части давали одинаковый zip и одинаковый package_sha256 Студии (дубль ищется по нему). Время — в <see cref="AgrLevelResult"/>.
    /// </summary>
    static JsonObject BuildManifest(AgrPart part, string stem, AgrPartSource source, AgrGltfResult gltf, AgrConvertResult result,
                                    GltfpackInfo gp, AgrConvertOptions options)
    {
        var levels = new JsonObject();
        var levelDesc = new JsonObject();
        foreach (var lr in result.Levels)
        {
            var spec = options.Levels.First(s => s.Name == lr.Name);
            var geo = lr.Geometry ?? spec;
            levelDesc[lr.Name] = lr.Simplified
                ? $"упрощение до {F(geo.SimplifyRatio)} треугольников, предел ошибки {F(spec.ErrorM)} м (-se {lr.SeRelative?.ToString("0.##########", CultureInfo.InvariantCulture)}), текстуры ≤ {spec.TextureLimit}" +
                  (lr.Fallback != null ? $"; геометрия {lr.Fallback.GeometryOf} (fallback)" : "")
                : $"полная геометрия, текстуры ≤ {spec.TextureLimit}" + (lr.Fallback != null ? $" (fallback к {lr.Fallback.GeometryOf})" : "");
            bool fullGeometry = !lr.Simplified;
            long dropped = result.TrianglesRef - lr.TrianglesGlb;
            var d = lr.Degenerate!;
            levels[lr.Name] = new JsonObject
            {
                ["triangles"] = lr.Triangles,
                ["bytes"] = lr.Bytes,
                ["images"] = lr.Images,
                ["ktx2_images"] = lr.Ktx2Images,
                ["image_max_px"] = lr.MaxImagePx,
                ["meshopt_views"] = lr.MeshoptViews,
                ["extensions_used"] = new JsonArray(lr.ExtensionsUsed.Select(e => (JsonNode)e!).ToArray()),
                ["err_fwd_max_m"] = R(lr.Forward!.Max, 5),
                ["err_fwd_p99_m"] = R(lr.Forward.P99, 5),
                ["err_rev_max_m"] = R(lr.Reverse!.Max, 5),
                ["err_rev_p99_m"] = R(lr.Reverse.P99, 5),
                ["err_rev_vertices_only_max_m"] = R(lr.ReverseVerticesOnlyMax, 5),
                ["rev_points"] = lr.Reverse.Points,
                ["texel_m"] = lr.TexelM,
                ["geometricError"] = lr.GeometricError,
                ["extras_agr"] = new JsonObject { ["geom_m"] = lr.GeomM, ["texel_m"] = lr.TexelM },
                ["texel_px_by_limit"] = lr.TexelPxByLimit,
                ["simplify"] = spec.Simplifies
                    ? new JsonObject
                    {
                        ["ratio"] = geo.SimplifyRatio, ["error_m"] = spec.ErrorM, ["se_relative"] = lr.SeRelative, ["attempts"] = lr.Attempts,
                        ["skipped"] = lr.SimplifySkipped.Length > 0 ? lr.SimplifySkipped : null,
                        ["fallback"] = lr.Fallback == null ? null : new JsonObject
                        {
                            ["geometry_of"] = lr.Fallback.GeometryOf,
                            ["after_attempts"] = lr.Fallback.Attempts,
                            ["last_error_m"] = lr.Fallback.ErrorM,
                            ["last_se_relative"] = lr.Fallback.SeRelative,
                            ["rule"] = "после последней сборки ошибка выше предела — уровень взял геометрию следующего, более точного уровня: L0 — последнюю сборку по флагам L1, если она в пределе L0, иначе итоговую геометрию L1; L1 — полную",
                        },
                    }
                    : null,
                ["degenerate"] = new JsonObject
                {
                    ["coincident"] = d.Coincident,
                    ["collinear"] = d.Collinear,
                    ["non_degenerate"] = d.NonDegenerate,
                    ["triangles_ref_writer"] = result.TrianglesRef,
                    ["triangles_ref_decoded"] = result.TrianglesRefDecoded,
                    ["triangles_glb"] = lr.TrianglesGlb,
                    ["triangles_twin"] = lr.Triangles,
                    ["dropped_by_gltfpack"] = dropped,
                    ["balance"] = "было: счётчик писателя glTF = декод буферов glTF; оставлено: JSON настоящего GLB = декод двойника = " +
                                  "non_degenerate + coincident + collinear (три отдельных счётчика); оставлено + выброшено = было, " +
                                  "выброшено ≥ 0, у полной геометрии выброшено = 0 (у упрощённой — упрощение и вырожденные gltfpack)",
                    ["balance_ok"] = result.TrianglesRef == result.TrianglesRefDecoded
                                     && lr.TrianglesGlb == lr.Triangles
                                     && d.NonDegenerate + d.Coincident + d.Collinear == lr.TrianglesGlb
                                     && dropped >= 0 && lr.TrianglesGlb + dropped == result.TrianglesRefDecoded
                                     && (!fullGeometry || dropped == 0),
                },
                ["twin_matches"] = lr.TwinMatches,
            };
        }
        var last = result.Levels[^1];
        var pl = part.Placement;
        double[] off = { pl?.E ?? 0, pl?.N ?? 0, pl?.DZ ?? 0 };
        var passport = part.Passport?.Features.FirstOrDefault()?.Properties.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var k in passport.Select(kv => kv.Key).Where(k => k.Equals("imageBase64", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            passport.Remove(k);
        }
        var partNode = new JsonObject
        {
            ["part"] = part.Name,
            ["short"] = ShortName(part.Name),
            ["code"] = "agr:" + part.Name,
            ["tiles"] = new JsonArray($"parts/{stem}"),
            ["point_msk77"] = part.Passport?.Point is { Length: >= 2 } pt ? new JsonObject { ["E"] = pt[0], ["N"] = pt[1] } : null,
            ["placement"] = pl == null ? null : new JsonObject
            {
                ["E"] = pl.E, ["N"] = pl.N, ["dZ"] = pl.DZ,
                ["rule"] = $"X = x + E, Y = y + N, Z = z + {(pl.DZ ?? 0).ToString(CultureInfo.InvariantCulture)}; (E, N) — точка geojson (первая координата — восток), z — как в FBX (Р6/Р7)",
            },
            ["passport"] = passport,
            ["bbox_local_zup_L2"] = new JsonObject
            {
                ["min"] = new JsonArray(last.BoxMinZup.Select(v => (JsonNode)R(v, 4)).ToArray()),
                ["max"] = new JsonArray(last.BoxMaxZup.Select(v => (JsonNode)R(v, 4)).ToArray()),
            },
            ["bbox_msk77_L2"] = new JsonObject
            {
                ["min"] = new JsonArray(last.BoxMinZup.Select((v, i) => (JsonNode)R(v + off[i], 4)).ToArray()),
                ["max"] = new JsonArray(last.BoxMaxZup.Select((v, i) => (JsonNode)R(v + off[i], 4)).ToArray()),
            },
            ["extent_m"] = R(result.ExtentM, 3),
            ["positions"] = result.Quantized
                ? $"16 бит (-vp 16): габарит {R(result.ExtentM, 1)} м ≤ {options.QuantizationLimitM} м — ошибка ≤ 1 см"
                : $"float32 без квантования (-noq): габарит {R(result.ExtentM, 1)} м > {options.QuantizationLimitM} м",
            ["triangles_ref"] = result.TrianglesRef,
            ["levels"] = levels,
            ["glass"] = GlassList(part, gltf),
            ["dropped"] = JsonSerializer.SerializeToNode(part.Dropped, AgrReport.Options),
            ["warnings"] = new JsonArray(part.Warnings.Concat(result.Warnings).Select(w => (JsonNode)w!).ToArray()),
        };
        var sourceNode = new JsonObject { ["name"] = Path.GetFileName(source.Path), ["kind"] = source.Kind.ToString() };
        if (source.Kind == AgrSourceKind.Zip)
        {
            using var fs = File.OpenRead(source.Path);
            sourceNode["bytes"] = fs.Length;
            sourceNode["sha256"] = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        var glass = partNode["glass"]!.AsArray().DeepClone();
        return new JsonObject
        {
            ["package"] = part.Name,
            ["revision"] = "C1c-1: пакет на часть (Р9), уровни gltfpack с пределом ошибки в метрах (н), квантование по габариту (и), симметричная ошибка (к, о), extras.agr (Р8, с)",
            ["generator"] = Generator,
            ["format"] = "OGC 3D Tiles 1.1 + glTF 2.0 (GLB: EXT_meshopt_compression, KHR_texture_basisu, KHR_texture_transform; " +
                         (result.Quantized ? "позиции 16 бит KHR_mesh_quantization)" : "позиции float32 без KHR_mesh_quantization)"),
            ["tileset"] = "tileset.json",
            ["crs"] = "МСК-77, высоты Балтийские",
            ["frame"] = "локальная МСК-77 без ECEF, Z вверх; transform части — перенос (E, N, ΔZ)",
            ["placement_rule"] = "X = x + E, Y = y + N, Z = z + ΔZ; (E, N) — точка geojson; ΔZ = h_relief",
            ["gltfpack"] = new JsonObject
            {
                ["version"] = gp.Version,
                ["exe_sha256"] = gp.Sha256,
                ["threads"] = "-tj постоянный (по умолчанию 8), не по числу ядер: байты KTX2 зависят от числа потоков",
                ["level_flags"] = new JsonObject(result.Levels.Select(l => KeyValuePair.Create(l.Name, (JsonNode?)string.Join(" ", l.Flags)))),
                ["commands"] = new JsonArray(result.Levels.Select(l => (JsonNode)new JsonObject
                {
                    ["level"] = l.Name, ["cmd"] = l.Command, ["twin_cmd"] = l.TwinCommand,
                }).ToArray()),
            },
            ["levels"] = levelDesc,
            ["extras_agr_schema"] = new JsonObject
            {
                ["where"] = "tileset.json: extras.agr у каждого тайла с содержимым (Р8); читает слой Студии (agrTiles/lod.ts)",
                ["geom_m"] = new JsonObject
                {
                    ["type"] = "number", ["unit"] = "m", ["precision_m"] = 1e-5,
                    ["meaning"] = "геометрическая ошибка уровня: max(прямое, обратное расстояние) до исходника; у последнего уровня — ошибка позиций квантования",
                },
                ["texel_m"] = new JsonObject
                {
                    ["type"] = "number", ["unit"] = "m", ["precision_m"] = 1e-6,
                    ["meaning"] = "размер текселя уровня на поверхности: Σ(A·метров на UV / сторона картинки материала в GLB уровня) / ΣA; сторона ≤ min(предел уровня, 4096)",
                },
            },
            ["geometric_error_method"] = "geometricError: у последнего уровня 0; у остальных max(err_fwd_max, err_rev_max, texel); у тайла части — радиус bbox; у tileset — диаметр",
            ["error_limit_rule"] = "(н) предел ошибки уровня строгий, без допуска: max(err_fwd_max, err_rev_max) ≤ simplify.error_m; до MaxErrorAttempts сборок с уменьшением -se, затем simplify.fallback; превышение — в warnings",
            ["min_triangles_to_simplify"] = options.MinTrianglesToSimplify,
            ["measure_method"] = MeasureMethod,
            ["measure_twin"] = "ошибки считаются по двойнику уровня: gltfpack с теми же флагами геометрии без сжатия и текстур; совпадение — twin_matches",
            ["glass_unconfirmed"] = "Стекло не подтверждено: константы паспорта Glasses (transparency понимается как непрозрачность) — вьюер может подменить материалы из списка glass по решению владельца без пересборки.",
            ["glass"] = glass,
            ["sources"] = new JsonArray(sourceNode),
            ["parts"] = new JsonArray(partNode),
        };
    }

    static JsonArray GlassList(AgrPart part, AgrGltfResult gltf)
    {
        var json = JsonNode.Parse(File.ReadAllText(gltf.GltfPath))!.AsObject();
        var list = new JsonArray();
        foreach (var m in json["materials"]?.AsArray() ?? new JsonArray())
        {
            var agr = m?["extras"]?["agr"];
            if (agr?["class"]?.GetValue<string>() != "glass") continue;
            var pbr = m!["pbrMetallicRoughness"];
            var bc = pbr?["baseColorFactor"]?.AsArray().Select(v => v!.GetValue<double>()).ToArray() ?? new double[] { 1, 1, 1, 1 };
            string srcMat = agr!["source_material"]!.GetValue<string>();
            JsonNode? passport = null;
            if (part.Passport?.Glasses is { } g)
            {
                lock (g)
                {
                    if (g.TryGetValue(srcMat, out var gv) || g.TryGetValue(AgrRules.NormalizeMaterialName(srcMat), out gv))
                    {
                        passport = gv.DeepClone();
                    }
                }
            }
            list.Add(new JsonObject
            {
                ["part"] = part.Name,
                ["material"] = m["name"]?.GetValue<string>(),
                ["baseColor"] = new JsonArray(R(bc[0], 4), R(bc[1], 4), R(bc[2], 4)),
                ["alpha"] = R(bc[3], 4),
                ["alphaMode"] = m["alphaMode"]?.GetValue<string>() ?? "OPAQUE",
                ["metallic"] = R(pbr?["metallicFactor"]?.GetValue<double>() ?? 1, 4),
                ["roughness"] = R(pbr?["roughnessFactor"]?.GetValue<double>() ?? 1, 4),
                ["source"] = new JsonObject
                {
                    ["source"] = "паспорт geojson, Glasses",
                    ["passport"] = passport,
                    ["transparency_as_opacity"] = "не подтверждено: transparency принята как непрозрачность (alpha)",
                    ["refraction"] = "в glTF не записан (нет transmission/volume)",
                },
                ["confirmed"] = false,
            });
        }
        return list;
    }

    /// <summary>Время записей zip — постоянное: иначе у каждого прогона свой sha zip.</summary>
    public static readonly DateTimeOffset ZipEntryTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static void WriteZip(string pkgDir, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var files = Directory.EnumerateFiles(pkgDir, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(pkgDir, f).Replace('\\', '/')))
            .OrderBy(f => f.Rel == "tileset.json" ? 0 : f.Rel == "manifest.json" ? 1 : 2).ThenBy(f => f.Rel, StringComparer.Ordinal);
        foreach (var (full, rel) in files)
        {
            var level = rel.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            var entry = zip.CreateEntry(rel, level);
            entry.LastWriteTime = ZipEntryTime;
            using var es = entry.Open();
            using var input = File.OpenRead(full);
            input.CopyTo(es, 1 << 20);
        }
    }
}
