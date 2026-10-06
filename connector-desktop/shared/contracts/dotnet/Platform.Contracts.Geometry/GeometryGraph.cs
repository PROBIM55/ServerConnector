using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Contracts.Geometry;

// Enum member names are deliberately short (M/Mm/Y/Z) so JsonNamingPolicy.CamelCase
// emits "m" / "mm" / "y" / "z" matching the TypeScript wire format.
// .NET 9 has [JsonStringEnumMemberName] for explicit per-value names; on net8.0
// we lean on the naming policy to do the same job.
public enum Units
{
    M,
    Mm,
}

public enum WorldUp
{
    Y,
    Z,
}

public sealed record Material(
    string Id,
    string Name,
    string? Color = null,
    double? Density = null,
    double? YoungsModulus = null,
    double? PoissonRatio = null);

public sealed record GeometryGraph(
    int SchemaVersion,
    Units Units,
    WorldUp WorldUp,
    IReadOnlyDictionary<string, Material> Materials,
    IReadOnlyList<SolidNode> Roots);

/// <summary>
/// Shared JsonSerializerOptions for GeometryGraph round-trip with the TypeScript side.
/// camelCase property names + JsonStringEnumConverter so kind discriminators
/// match `kind: "polyline"` etc. on the wire.
/// </summary>
public static class GeometryJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };
}
