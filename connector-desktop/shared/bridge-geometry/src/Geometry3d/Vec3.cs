using System;

namespace Platform.Bridge.Geometry.Geometry3d;

/// <summary>
/// Pure 3D-вектор / точка с double-precision. Без зависимости от
/// Tekla.Structures.Geometry3d и System.Numerics.Vectors NuGet —
/// делает Placement testable без CAD-runtime'а. Phase E конвертирует
/// Vec3 → Tekla.Structures.Geometry3d.Point на границе TeklaShim.
/// </summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 Zero => new(0, 0, 0);
    public static Vec3 UnitX => new(1, 0, 0);
    public static Vec3 UnitY => new(0, 1, 0);
    public static Vec3 UnitZ => new(0, 0, 1);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 v, double k) => new(v.X * k, v.Y * k, v.Z * k);
    public static Vec3 operator *(double k, Vec3 v) => v * k;

    public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);

    public Vec3 Normalize()
    {
        var len = Length();
        if (len < 1e-9)
            throw new InvalidOperationException("Cannot normalize zero-length vector");
        return new Vec3(X / len, Y / len, Z / len);
    }

    public Vec3 Cross(Vec3 b)
        => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);

    public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
}
