using System.Text.Json.Serialization;

namespace Platform.Contracts.Geometry;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RectProfile), typeDiscriminator: "rect")]
[JsonDerivedType(typeof(CircleProfile), typeDiscriminator: "circle")]
[JsonDerivedType(typeof(PolygonProfile), typeDiscriminator: "polygon")]
public abstract record Profile2D;

public sealed record RectProfile(double Width, double Height) : Profile2D;
public sealed record CircleProfile(double Radius) : Profile2D;
public sealed record PolygonProfile(IReadOnlyList<Vec2> Points) : Profile2D;
