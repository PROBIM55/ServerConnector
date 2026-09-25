using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Connector.AgrConversion.Service;

/// <summary>Уровень в отчёте части.</summary>
public sealed class AgrPartReportLevel
{
    public required string Name { get; init; }
    public long Triangles { get; init; }
    public long Bytes { get; init; }
    public int Images { get; init; }
    public int ImageMaxPx { get; init; }

    /// <summary>Ошибка геометрии, см: max(прямая, обратная) — наибольшая; p99 — 99-й процентиль.</summary>
    public double ErrorMaxCm { get; init; }
    public double ErrorP99Cm { get; init; }

    /// <summary>Размер текселя на поверхности, см.</summary>
    public double TexelCm { get; init; }

    /// <summary>Предел ошибки уровня (н), см; null — уровень без упрощения.</summary>
    public double? ErrorLimitCm { get; init; }

    public bool Simplified { get; init; }

    /// <summary>Описание уровня из манифеста («упрощение до 0.5 …», «полная геометрия …»).</summary>
    public string Description { get; init; } = "";

    /// <summary><c>simplify.fallback</c>: уровень взял геометрию более точного уровня; null — нет.</summary>
    public string? FallbackGeometryOf { get; init; }
    public int FallbackAttempts { get; init; }
    public double? FallbackLastErrorCm { get; init; }

    /// <summary>Малая часть: упрощение пропущено (порог треугольников), текст из манифеста.</summary>
    public string? SimplifySkipped { get; init; }

    /// <summary>Ошибка выше предела (н) — после всех сборок и fallback.</summary>
    public bool LimitExceeded => ErrorLimitCm is double lim && ErrorMaxCm > lim + 1e-9;
}

/// <summary>
/// Отчёт части для окна «Отчёт — &lt;часть&gt;» (кадр 4 макета C2). Только читает отчёт чтения части
/// (<see cref="AgrReport"/>, <c>…report.json</c>) и манифест C1c-1 (<c>…manifest.json</c> рядом или
/// <c>manifest.json</c> в zip); конвертацию не запускает.
/// </summary>
public sealed class AgrPartReport
{
    public string Part { get; init; } = "";
    public string? SourceName { get; init; }
    public long? SourceBytes { get; init; }

    /// <summary>Треугольники исходника (писатель glTF, без коллизий UCX_).</summary>
    public long TrianglesSource { get; init; }
    public IReadOnlyList<AgrPartReportLevel> Levels { get; init; } = Array.Empty<AgrPartReportLevel>();

    public double ExtentM { get; init; }

    /// <summary>Позиции: 16 бит или float32 — текст манифеста.</summary>
    public string Positions { get; init; } = "";

    /// <summary>Точность последнего (полного) уровня, см — ошибка позиций квантования.</summary>
    public double? AccuracyCm => Levels.Count == 0 ? null : Levels[^1].ErrorMaxCm;

    /// <summary>Файлов картинок в части; настоящих; однотонных заглушек (заменены константой); без граней.</summary>
    public int? TextureFiles { get; init; }
    public int? TexturesReal { get; init; }
    public int? TextureStubs { get; init; }
    public int? TexturesUnused { get; init; }

    /// <summary>Заглушки — картинки заменены цветом, в модель не идут.</summary>
    public IReadOnlyList<string> StubFiles { get; init; } = Array.Empty<string>();

    /// <summary>Отброшено: картинки без граней, свет FBX, коллизии UCX_, emissive ERM (текстом).</summary>
    public IReadOnlyList<string> Dropped { get; init; } = Array.Empty<string>();

    /// <summary>Предупреждения: <c>simplify.fallback</c> и превышение предела (н) — первыми, затем манифест и чтение части.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool HasFallback => Levels.Any(l => l.FallbackGeometryOf != null);
    public bool LimitExceeded => Levels.Any(l => l.LimitExceeded);

    /// <summary>Отчёт без результата: ошибка, отмена или файлы отчёта не прочитаны. Остальные поля — что известно.</summary>
    public bool IsFailure => Error != null;
    public string? Error { get; init; }

    /// <summary>Этап, на котором строка остановилась («Уровни — gltfpack L1», «проверка ввода»).</summary>
    public string? FailedStage { get; init; }

    /// <summary>Сколько успела, %.</summary>
    public int? Percent { get; init; }
    public AgrRowState? State { get; init; }
    public string? SourcePath { get; init; }

    public string? ZipPath { get; init; }
    public string? ReportPath { get; init; }
    public string? ManifestPath { get; init; }
    public string? Folder => ZipPath != null ? Path.GetDirectoryName(ZipPath) : null;

    /// <summary>
    /// Собирает отчёт из файлов. Манифест — из <paramref name="manifestPath"/>, если есть, иначе из <c>manifest.json</c>
    /// в <paramref name="zipPath"/>. Нет ни того, ни другого — <see cref="FileNotFoundException"/>; отчёт чтения части
    /// необязателен (без него нет числа заглушек).
    /// </summary>
    public static AgrPartReport Load(string? manifestPath, string? reportPath, string? zipPath)
    {
        JsonObject manifest = ReadManifest(manifestPath, zipPath);
        JsonObject? report = reportPath != null && File.Exists(reportPath)
            ? JsonNode.Parse(File.ReadAllText(reportPath))?.AsObject()
            : null;
        return FromJson(manifest, report, manifestPath, reportPath, zipPath);
    }

    /// <summary>Отчёт-ошибка строки без результата (окно «Отчёт» у строки с ошибкой или отменой).</summary>
    public static AgrPartReport Failure(string part, string? sourcePath, AgrRowState? state, string error, string? stage, int? percent,
                                        string? zipPath = null, string? reportPath = null, string? manifestPath = null) => new()
    {
        Part = part,
        SourceName = sourcePath != null ? Path.GetFileName(sourcePath) : null,
        SourcePath = sourcePath,
        State = state,
        Error = error,
        FailedStage = stage,
        Percent = percent,
        ZipPath = zipPath,
        ReportPath = reportPath,
        ManifestPath = manifestPath,
    };

    /// <summary><see cref="Load"/> без исключений: файлы пропали или битые — отчёт-ошибка с причиной.</summary>
    public static AgrPartReport TryLoad(string part, string? sourcePath, string? manifestPath, string? reportPath, string? zipPath)
    {
        try
        {
            return Load(manifestPath, reportPath, zipPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return Failure(part, sourcePath, AgrRowState.Done, "отчёт не прочитан: " + ex.Message, null, 100, zipPath, reportPath, manifestPath);
        }
    }

    static JsonObject ReadManifest(string? manifestPath, string? zipPath)
    {
        if (manifestPath != null && File.Exists(manifestPath))
        {
            return JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject() ?? throw new InvalidDataException($"{manifestPath}: пустой манифест");
        }
        if (zipPath != null && File.Exists(zipPath))
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException($"{Path.GetFileName(zipPath)}: в пакете нет manifest.json");
            using var s = entry.Open();
            return JsonNode.Parse(s)?.AsObject() ?? throw new InvalidDataException($"{Path.GetFileName(zipPath)}: пустой manifest.json");
        }
        throw new FileNotFoundException("нет ни манифеста части, ни её .glb.zip", manifestPath ?? zipPath);
    }

    static double Cm(double? m) => m is double v ? Math.Round(v * 100, 2) : 0;
    // Числа — через текст JSON: у узлов, собранных в памяти (JsonValue<int>), TryGetValue<double> не сработает.
    static double? D(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    static long L(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? long.TryParse(v.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : (long)(D(n) ?? 0)
            : 0;
    static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static string F(double v) => v.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));

    public static AgrPartReport FromJson(JsonObject manifest, JsonObject? report, string? manifestPath = null,
                                         string? reportPath = null, string? zipPath = null)
    {
        var part = manifest["parts"]?.AsArray().FirstOrDefault()?.AsObject() ?? throw new InvalidDataException("в манифесте нет parts[0]");
        var source = manifest["sources"]?.AsArray().FirstOrDefault()?.AsObject();
        var levelDesc = manifest["levels"] as JsonObject;

        var levels = new List<AgrPartReportLevel>();
        var flagged = new List<string>();
        if (part["levels"] is JsonObject lv)
        {
            foreach (var (name, node) in lv.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (node is not JsonObject l) continue;
                double errMax = Math.Max(D(l["err_fwd_max_m"]) ?? 0, D(l["err_rev_max_m"]) ?? 0);
                double errP99 = Math.Max(D(l["err_fwd_p99_m"]) ?? 0, D(l["err_rev_p99_m"]) ?? 0);
                var simplify = l["simplify"] as JsonObject;
                var fallback = simplify?["fallback"] as JsonObject;
                double? limit = D(simplify?["error_m"]) is double lm && lm > 0 ? Cm(lm) : null;
                var level = new AgrPartReportLevel
                {
                    Name = name,
                    Triangles = L(l["triangles"]),
                    Bytes = L(l["bytes"]),
                    Images = (int)L(l["images"]),
                    ImageMaxPx = (int)L(l["image_max_px"]),
                    ErrorMaxCm = Cm(errMax),
                    ErrorP99Cm = Cm(errP99),
                    TexelCm = Cm(D(l["texel_m"])),
                    ErrorLimitCm = limit,
                    Simplified = simplify != null,
                    Description = S(levelDesc?[name]) ?? "",
                    FallbackGeometryOf = S(fallback?["geometry_of"]),
                    FallbackAttempts = (int)L(fallback?["after_attempts"]),
                    FallbackLastErrorCm = fallback == null ? null : Cm(D(fallback["last_error_m"])),
                    SimplifySkipped = S(simplify?["skipped"]),
                };
                levels.Add(level);
                if (level.FallbackGeometryOf != null)
                {
                    flagged.Add($"{name}: simplify.fallback — за {level.FallbackAttempts} {AgrConverterText.Plural(level.FallbackAttempts, "сборку", "сборки", "сборок")} ошибка {F(level.FallbackLastErrorCm ?? 0)} см " +
                                $"выше предела {F(limit ?? 0)} см, взята геометрия {level.FallbackGeometryOf}");
                }
                if (level.LimitExceeded)
                {
                    flagged.Add($"{name}: ошибка {F(level.ErrorMaxCm)} см выше предела {F(limit!.Value)} см (н)");
                }
            }
        }
        var warnings = new List<string>(flagged);
        foreach (var w in part["warnings"]?.AsArray() ?? new JsonArray())
        {
            if (S(w) is { } text && !warnings.Contains(text)) warnings.Add(text);
        }
        foreach (var w in report?["warnings"]?.AsArray() ?? new JsonArray())
        {
            if (S(w) is { } text && !warnings.Contains(text)) warnings.Add(text);
        }

        var dropped = new List<string>();
        var dm = part["dropped"] as JsonObject ?? report?["dropped"] as JsonObject;
        if (dm != null)
        {
            var unused = dm["unused_textures"]?.AsArray().Select(S).Where(s => s != null).ToList() ?? new();
            if (unused.Count > 0) dropped.Add($"картинки без граней: {unused.Count} ({string.Join(", ", unused.Take(5))}{(unused.Count > 5 ? ", …" : "")})");
            var light = dm["light_fbx"]?.AsArray().Select(S).Where(s => s != null).ToList() ?? new();
            if (light.Count > 0) dropped.Add($"свет FBX: {string.Join(", ", light)}");
            if (dm["collision"] is JsonObject col && col.Count > 0)
            {
                long tris = col.Sum(kv => L(kv.Value?["triangles"]));
                long objs = col.Sum(kv => L(kv.Value?["objects"]));
                dropped.Add($"коллизии UCX_: {objs} объектов, {tris} треугольников");
            }
            if (dm["emissive"] is JsonArray em && em.Count > 0) dropped.Add($"emissive в R карт ERM: {em.Count} карт — не переносится");
        }

        var ts = report?["textures_summary"] as JsonObject;
        var stubs = report?["stubs"]?.AsArray().Select(s => S(s?["file"])).Where(s => s != null).Select(s => s!).ToList() ?? new();

        return new AgrPartReport
        {
            Part = S(part["part"]) ?? S(manifest["package"]) ?? "",
            SourceName = S(source?["name"]),
            SourceBytes = source?["bytes"] is JsonNode b ? L(b) : null,
            TrianglesSource = L(part["triangles_ref"]),
            Levels = levels,
            ExtentM = D(part["extent_m"]) ?? 0,
            Positions = S(part["positions"]) ?? "",
            TextureFiles = ts == null ? null : (int)L(ts["files"]),
            TexturesReal = ts == null ? null : (int)L(ts["real"]),
            TextureStubs = report?["stubs"] is JsonArray ? stubs.Count : null,
            TexturesUnused = ts == null ? null : (int)L(ts["unused"]),
            StubFiles = stubs,
            Dropped = dropped,
            Warnings = warnings,
            ZipPath = zipPath,
            ReportPath = reportPath,
            ManifestPath = manifestPath,
        };
    }
}
