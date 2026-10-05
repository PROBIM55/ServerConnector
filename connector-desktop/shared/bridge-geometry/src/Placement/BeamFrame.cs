using System;
using Platform.Bridge.Geometry.Geometry3d;

namespace Platform.Bridge.Geometry.Placement;

/// <summary>
/// Локальная система координат балки: origin + ортогональный базис (ex, ey, ez).
/// ex — вдоль оси (от start к end), ez — "вверх" (ортогонально к ex и горизонтали),
/// ey — поперёк (горизонтально). Точка на плите вычисляется как
/// origin + ex·s + ey·w + ez·k.
/// </summary>
/// <remarks>
/// Алгоритм построения базиса портирован из decomp Step4 (TeklaBridge.exe :1105-1113):
/// ex = (end - start).normalized; ey = (ex × worldUp).normalized; ez = (ey × ex).normalized.
/// Если ex почти параллелен worldUp (вертикальная балка) — fallback ey = unit(0,1,0).
/// </remarks>
public readonly record struct BeamFrame(Vec3 Origin, Vec3 Ex, Vec3 Ey, Vec3 Ez, double AxisLength)
{
    /// <summary>
    /// Построить локальный базис из двух концов осевой линии балки.
    /// </summary>
    public static BeamFrame Build(Vec3 start, Vec3 end)
    {
        var diff = end - start;
        var axisLength = diff.Length();
        if (axisLength < 1e-6)
            throw new InvalidOperationException("Beam axis has zero length");

        var ex = diff.Normalize();
        var worldUp = Vec3.UnitZ;
        var rawEy = ex.Cross(worldUp);
        // Fallback when axis is parallel to world-Z (vertical beam).
        var ey = rawEy.Length() < 1e-6 ? Vec3.UnitY : rawEy.Normalize();
        var ez = ey.Cross(ex).Normalize();
        return new BeamFrame(start, ex, ey, ez, axisLength);
    }

    /// <summary>
    /// Точка на оси балки на расстоянии s от start (s in [0, AxisLength]).
    /// </summary>
    public Vec3 AxisPoint(double s) => Origin + Ex * s;

    /// <summary>
    /// Произвольная точка в локальной системе: s вдоль оси, k вверх (по ez),
    /// w поперёк (по ey). Все в мм.
    /// </summary>
    public Vec3 LocalPoint(double s, double k, double w = 0)
        => Origin + Ex * s + Ez * k + Ey * w;

    /// <summary>
    /// Возвращает новый BeamFrame с осями Ey/Ez повёрнутыми на rollRad
    /// вокруг Ex. Используется для box roll (boxRotationDeg) — глобальный
    /// поворот всей коробки вокруг продольной оси. Положительный угол
    /// поворачивает Ey → +Ez, Ez → -Ey (стандарт right-hand rule).
    /// </summary>
    public BeamFrame WithRoll(double rollRad)
    {
        if (Math.Abs(rollRad) < 1e-9) return this;
        var cos = Math.Cos(rollRad);
        var sin = Math.Sin(rollRad);
        var newEy = Ey * cos + Ez * sin;
        var newEz = Ey * (-sin) + Ez * cos;
        return new BeamFrame(Origin, Ex, newEy, newEz, AxisLength);
    }
}
