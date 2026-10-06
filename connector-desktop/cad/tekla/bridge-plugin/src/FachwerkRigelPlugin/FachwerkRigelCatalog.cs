#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace Structura.Tekla.Fachwerk;

public sealed class FachwerkRigelCatalog
{
    public const string CurrentSchema = "fachwerk-rigel-catalog/v1";

    public string Schema { get; set; }
    public string VariantId { get; set; }
    public string WorkspaceId { get; set; }
    public string GenerationId { get; set; }
    public List<FachwerkRigelSupportProfile> Profiles { get; set; } = new List<FachwerkRigelSupportProfile>();
    public List<FachwerkRigelLayout> Layouts { get; set; } = new List<FachwerkRigelLayout>();

    public static FachwerkRigelCatalog LoadEmbedded()
    {
        var assembly = typeof(FachwerkRigelCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "Structura.Tekla.Fachwerk.FachwerkRigelCatalog.json");
        if (stream == null)
            throw new InvalidOperationException("В DLL отсутствует встроенный каталог ригелей.");
        using var reader = new StreamReader(stream);
        var serializer = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 512,
        };
        var catalog = serializer.Deserialize<FachwerkRigelCatalog>(reader.ReadToEnd());
        catalog?.Validate();
        return catalog;
    }

    public FachwerkRigelSupportProfile RequireProfile(int profileNumber)
    {
        for (var index = 0; index < Profiles.Count; index++)
            if (Profiles[index].ProfileNumber == profileNumber) return Profiles[index];
        throw new InvalidOperationException("В каталоге отсутствует ось стойки СФ" + profileNumber + ".");
    }

    private void Validate()
    {
        if (!string.Equals(Schema, CurrentSchema, StringComparison.Ordinal))
            throw new InvalidOperationException("Неподдерживаемая схема каталога ригелей: " + Schema);
        if (Profiles == null || Profiles.Count != 47)
            throw new InvalidOperationException("Каталог должен содержать 47 осей стоек.");
        if (Layouts == null || Layouts.Count != 3)
            throw new InvalidOperationException("Каталог должен содержать три цепочки ригелей.");
        foreach (var profile in Profiles) profile.Validate();
        foreach (var layout in Layouts) layout.Validate(this);
    }
}

public sealed class FachwerkRigelSupportProfile
{
    public int ProfileNumber { get; set; }
    public string SourceProfileKey { get; set; }
    public string Mark { get; set; }
    public FachwerkRigelPoint Origin { get; set; }
    public FachwerkRigelPoint AxisX { get; set; }
    public FachwerkRigelPoint AxisY { get; set; }
    public List<FachwerkRigelPoint> InnerPath { get; set; } = new List<FachwerkRigelPoint>();
    public List<FachwerkRigelPoint> OuterPath { get; set; } = new List<FachwerkRigelPoint>();

    public void Validate()
    {
        if (ProfileNumber < 1 || ProfileNumber > 47 || Origin == null || AxisX == null || AxisY == null)
            throw new InvalidOperationException("Ось стойки в каталоге задана неполно.");
        if (InnerPath == null || InnerPath.Count < 2 || OuterPath == null || OuterPath.Count < 2)
            throw new InvalidOperationException("У стойки СФ" + ProfileNumber + " отсутствуют опорные кромки.");
    }
}

public sealed class FachwerkRigelLayout
{
    public string Code { get; set; }
    public string Label { get; set; }
    public string SourceReference { get; set; }
    public string Profile { get; set; }
    public string Material { get; set; }
    public string ClassName { get; set; }
    public double BaseElevationMm { get; set; }
    public double TransverseOffsetMm { get; set; }
    public List<FachwerkRigelControlPoint> Points { get; set; } = new List<FachwerkRigelControlPoint>();

    public void Validate(FachwerkRigelCatalog catalog)
    {
        var expected = string.Equals(Code, "RS2", StringComparison.OrdinalIgnoreCase) ? 2 : 16;
        if (string.IsNullOrWhiteSpace(Code) || string.IsNullOrWhiteSpace(Profile) ||
            Points == null || Points.Count != expected)
        {
            throw new InvalidOperationException("Схема ригеля '" + Code + "' задана неполно.");
        }
        for (var index = 0; index < Points.Count; index++)
        {
            var point = Points[index];
            if (point.Ordinal != index)
                throw new InvalidOperationException("Нарушен порядок точек ригеля '" + Code + "'.");
            catalog.RequireProfile(point.LeftProfileNumber);
            catalog.RequireProfile(point.RightProfileNumber);
        }
    }
}

public sealed class FachwerkRigelControlPoint
{
    public int Ordinal { get; set; }
    public string Role { get; set; }
    public string Label { get; set; }
    public int LeftProfileNumber { get; set; }
    public int RightProfileNumber { get; set; }
    public double StationT { get; set; }
    public double ElevationDeltaMm { get; set; }
    public double CalculatedTransverseOffsetMm { get; set; }
    public FachwerkRigelPoint ResolvedPosition { get; set; }
}

public sealed class FachwerkRigelPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
