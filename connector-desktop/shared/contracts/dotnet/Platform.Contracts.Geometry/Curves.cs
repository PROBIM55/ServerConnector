using System.Text.Json.Serialization;

namespace Platform.Contracts.Geometry;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Polyline), typeDiscriminator: "polyline")]
[JsonDerivedType(typeof(Arc), typeDiscriminator: "arc")]
[JsonDerivedType(typeof(Bezier), typeDiscriminator: "bezier")]
public abstract record Curve;

public sealed record Polyline(IReadOnlyList<Vec3> Points, bool Closed) : Curve;

public sealed record Arc(
    Vec3 Center,
    Vec3 Normal,
    double Radius,
    double StartAngle,
    double EndAngle) : Curve;

public sealed record Bezier(IReadOnlyList<Vec3> ControlPoints, int Degree) : Curve;
