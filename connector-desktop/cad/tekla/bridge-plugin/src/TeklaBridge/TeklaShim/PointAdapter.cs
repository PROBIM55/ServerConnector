using Platform.Bridge.Geometry.Geometry3d;
using TS = Tekla.Structures.Geometry3d;

namespace TeklaBridge.TeklaShim;

/// <summary>
/// Конверсия между нашим pure <see cref="Vec3"/> и Tekla
/// <see cref="TS.Point"/>/<see cref="TS.Vector"/>. Единственный мост
/// между Geometry-слоем (без Tekla SDK) и Tekla-слоем; везде ещё
/// используется этот адаптер чтобы остальной код в TeklaBridge мог
/// думать в терминах Vec3.
/// </summary>
internal static class PointAdapter
{
    public static TS.Point ToPoint(this Vec3 v) => new(v.X, v.Y, v.Z);

    public static TS.Vector ToVector(this Vec3 v) => new(v.X, v.Y, v.Z);

    public static Vec3 ToVec3(this TS.Point p) => new(p.X, p.Y, p.Z);

    public static Vec3 ToVec3(this TS.Vector v) => new(v.X, v.Y, v.Z);
}
