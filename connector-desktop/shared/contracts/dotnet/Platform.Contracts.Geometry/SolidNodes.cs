using System.Text.Json.Serialization;

namespace Platform.Contracts.Geometry;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Box), typeDiscriminator: "box")]
[JsonDerivedType(typeof(Cylinder), typeDiscriminator: "cylinder")]
[JsonDerivedType(typeof(Sweep), typeDiscriminator: "sweep")]
[JsonDerivedType(typeof(Extrude), typeDiscriminator: "extrude")]
[JsonDerivedType(typeof(Loft), typeDiscriminator: "loft")]
[JsonDerivedType(typeof(Boolean3), typeDiscriminator: "boolean")]
[JsonDerivedType(typeof(MirrorN), typeDiscriminator: "mirror")]
[JsonDerivedType(typeof(ArrayN), typeDiscriminator: "array")]
[JsonDerivedType(typeof(NamedSolid), typeDiscriminator: "named")]
public abstract record SolidNode;

// ---------------- Leaves ----------------

public sealed record Box(Vec3 Size, Transform Transform) : SolidNode;

public sealed record Cylinder(double Radius, double Height, Transform Transform) : SolidNode;

public sealed record Sweep(Profile2D Profile, Curve Path, Transform Transform) : SolidNode;

public sealed record Extrude(
    Profile2D Profile,
    Vec3 Direction,
    double Length,
    Transform Transform) : SolidNode;

public sealed record LoftSection(Profile2D Profile, Transform Transform);

public sealed record Loft(IReadOnlyList<LoftSection> Profiles, bool Closed) : SolidNode;

// ---------------- Composites ----------------

public enum BooleanOp
{
    Union,
    Subtract,
    Intersect,
}

public sealed record Boolean3(BooleanOp Op, SolidNode A, SolidNode B) : SolidNode;

public sealed record MirrorPlane(Vec3 Point, Vec3 Normal);
public sealed record MirrorN(SolidNode Target, MirrorPlane Plane) : SolidNode;

public sealed record ArrayN(SolidNode Target, int Count, Transform Step) : SolidNode;

// ---------------- Named (selectable) wrapper ----------------

public sealed record MaterialRef(string Id);

public sealed record NamedSolid(
    string Id,
    SolidNode Node,
    MaterialRef? Material = null,
    IReadOnlyDictionary<string, object?>? Props = null) : SolidNode;
