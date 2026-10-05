#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Structura.Tekla.Fachwerk;

/// <summary>
/// The catalogue is an install-time artefact exported once from the accepted KM
/// profiles. This keeps the production component independent from the browser,
/// PostgreSQL and any live Tekla scan.
/// </summary>
public sealed class FachwerkColumnProfileCatalog
{
    public const string CurrentSchema = "fachwerk-column-catalog/v2";

    public string Schema { get; set; }
    public List<FachwerkColumnProfileDefinition> Profiles { get; set; } = new List<FachwerkColumnProfileDefinition>();

    public static FachwerkColumnProfileCatalog Load(string requestedPath)
    {
        var path = ResolvePath(requestedPath);
        if (!File.Exists(path))
            throw new InvalidOperationException("Не найден каталог траекторий стоек: " + path);

        var json = File.ReadAllText(path);
        var result = new JavaScriptSerializer().Deserialize<FachwerkColumnProfileCatalog>(json);
        if (result == null || result.Profiles == null || result.Profiles.Count == 0)
            throw new InvalidOperationException("Каталог траекторий стоек пуст: " + path);
        if (!string.Equals(result.Schema, CurrentSchema, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Каталог траекторий стоек имеет устаревшую схему '" + result.Schema + "'; ожидается '" + CurrentSchema + "'.");
        return result;
    }

    public FachwerkColumnProfileDefinition Require(string profileKey)
    {
        var normalized = (profileKey ?? string.Empty).Trim();
        FachwerkColumnProfileDefinition profile = null;
        for (var index = 0; index < Profiles.Count; index++)
        {
            if (string.Equals(Profiles[index].Key, normalized, StringComparison.OrdinalIgnoreCase))
            {
                profile = Profiles[index];
                break;
            }
        }
        if (profile == null)
        {
            for (var index = 0; index < Profiles.Count; index++)
            {
                if (string.Equals(Profiles[index].Mark, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    profile = Profiles[index];
                    break;
                }
            }
        }
        if (profile == null)
            throw new InvalidOperationException("В каталоге нет профиля стойки '" + normalized + "'.");
        profile.Validate();
        return profile;
    }

    private static string ResolvePath(string requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
            return Environment.ExpandEnvironmentVariables(requestedPath.Trim());
        var assemblyDirectory = Path.GetDirectoryName(typeof(FachwerkColumnProfileCatalog).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            return Path.Combine(assemblyDirectory, "FachwerkColumnProfiles.json");
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FachwerkColumnProfiles.json");
    }
}

public sealed class FachwerkColumnProfileDefinition
{
    public string Key { get; set; }
    public string Mark { get; set; }
    public FachwerkColumnSectionTransitionDefinition SectionTransition { get; set; }
    public List<FachwerkColumnPathDefinition> Paths { get; set; } = new List<FachwerkColumnPathDefinition>();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key))
            throw new InvalidOperationException("У профиля стойки отсутствует ключ.");
        if (Paths == null || Paths.Count == 0)
            throw new InvalidOperationException("У профиля '" + Key + "' нет траекторий деталей.");
        foreach (var path in Paths) path.Validate(Key);
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Paths.Count; index++)
        {
            roles.Add(Paths[index].NormalizedRole());
        }
        foreach (var required in new[] { "inner-web", "outer-web", "inner-flange", "outer-flange" })
        {
            if (!roles.Contains(required))
                throw new InvalidOperationException("У профиля '" + Key + "' отсутствует обязательная траектория '" + required + "'.");
        }
        if (SectionTransition == null)
            throw new InvalidOperationException("У профиля '" + Key + "' не задан переход сечения 450/380 мм.");
        SectionTransition.Validate(Key, RequirePath("inner-flange"));
    }

    public FachwerkColumnPathDefinition RequirePath(string role)
    {
        var normalized = (role ?? string.Empty).Trim().ToLowerInvariant();
        FachwerkColumnPathDefinition path = null;
        for (var index = 0; index < Paths.Count; index++)
        {
            if (Paths[index].NormalizedRole() == normalized)
            {
                path = Paths[index];
                break;
            }
        }
        if (path == null)
            throw new InvalidOperationException("У профиля '" + Key + "' отсутствует траектория '" + normalized + "'.");
        return path;
    }
}

public sealed class FachwerkColumnSectionTransitionDefinition
{
    public double StartY { get; set; }
    public double EndY { get; set; }
    public string LowerWebProfile { get; set; }
    public string UpperWebProfile { get; set; }

    public void Validate(string profileKey, FachwerkColumnPathDefinition innerFlange)
    {
        if (!IsFinite(StartY) || !IsFinite(EndY) || EndY <= StartY)
            throw new InvalidOperationException("У профиля '" + profileKey + "' некорректные координаты перехода сечения.");
        if (string.IsNullOrWhiteSpace(LowerWebProfile) || string.IsNullOrWhiteSpace(UpperWebProfile))
            throw new InvalidOperationException("У профиля '" + profileKey + "' не заданы профили стенок до/после перехода.");

        const double tolerance = 0.25;
        var matches = 0;
        for (var index = 0; index < innerFlange.Primitives.Count; index++)
        {
            var primitive = innerFlange.Primitives[index];
            if (primitive.NormalizedKind() != "line") continue;
            if (Math.Abs(primitive.Start.Y - StartY) <= tolerance &&
                Math.Abs(primitive.End.Y - EndY) <= tolerance)
            {
                matches++;
            }
        }
        if (matches != 1)
        {
            throw new InvalidOperationException(
                "У профиля '" + profileKey + "' переход сечения не совпал ровно с одним сегментом внутренней траектории.");
        }
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

public sealed class FachwerkColumnPathDefinition
{
    public string Role { get; set; }
    public string Profile { get; set; }
    public string Material { get; set; }
    public string ClassName { get; set; }
    /// <summary>
    /// Offset from the facade-profile plane along its local normal. The profile
    /// path itself stays in the KM facade plane; the four physical plates of a
    /// column are separated only by this cross-section coordinate.
    /// </summary>
    public double NormalOffset { get; set; }
    public List<FachwerkColumnPrimitiveDefinition> Primitives { get; set; } = new List<FachwerkColumnPrimitiveDefinition>();

    public string NormalizedRole() => (Role ?? string.Empty).Trim().ToLowerInvariant();

    public void Validate(string profileKey)
    {
        if (string.IsNullOrWhiteSpace(Role))
            throw new InvalidOperationException("У профиля '" + profileKey + "' есть траектория без роли.");
        if (string.IsNullOrWhiteSpace(Profile))
            throw new InvalidOperationException("У траектории '" + Role + "' профиля '" + profileKey + "' не задан Tekla-профиль.");
        if (Primitives == null || Primitives.Count == 0)
            throw new InvalidOperationException("У траектории '" + Role + "' профиля '" + profileKey + "' нет примитивов.");
        foreach (var primitive in Primitives) primitive.Validate(profileKey, Role);
    }
}

public sealed class FachwerkColumnPrimitiveDefinition
{
    /// <summary>line or arc. Arc is circular and uses Center + signed SweepDeg.</summary>
    public string Kind { get; set; }
    public FachwerkColumnLocalPoint Start { get; set; }
    public FachwerkColumnLocalPoint End { get; set; }
    public FachwerkColumnLocalPoint Center { get; set; }
    public double SweepDeg { get; set; }

    public string NormalizedKind() => (Kind ?? string.Empty).Trim().ToLowerInvariant();

    public void Validate(string profileKey, string role)
    {
        if (Start == null || End == null || !Start.IsFinite() || !End.IsFinite())
            throw new InvalidOperationException("У примитива '" + role + "' профиля '" + profileKey + "' отсутствуют корректные начало/конец.");
        var kind = NormalizedKind();
        if (kind != "line" && kind != "arc")
            throw new InvalidOperationException("Примитив '" + role + "' профиля '" + profileKey + "' имеет неизвестный тип '" + Kind + "'.");
        if (kind == "arc")
        {
            if (Center == null || !Center.IsFinite() || Math.Abs(SweepDeg) < 1e-6)
                throw new InvalidOperationException("Дуга '" + role + "' профиля '" + profileKey + "' должна иметь центр и ненулевой SweepDeg.");
        }
    }
}

public sealed class FachwerkColumnLocalPoint
{
    public double X { get; set; }
    public double Y { get; set; }

    public bool IsFinite() => !double.IsNaN(X) && !double.IsInfinity(X) &&
        !double.IsNaN(Y) && !double.IsInfinity(Y);

    public FachwerkColumnLocalPoint Clone() => new FachwerkColumnLocalPoint { X = X, Y = Y };
}
