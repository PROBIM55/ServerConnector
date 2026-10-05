using Platform.Bridge.Geometry.Geometry3d;

namespace Platform.Bridge.Geometry.Cuts;

/// <summary>
/// Описание одного cutter-полигона в мировых координатах. Tekla-сторона
/// (TeklaShim) превращает его в ContourPlate с профилем PL{Thickness},
/// делает Insert + BooleanCut по <see cref="TargetPartIndex"/>-плите.
///
/// Vertices содержит 3 (треугольник для width-cut) или 4 (трапеция для
/// thickness-cut) точки. Все в мировой системе координат.
/// </summary>
/// <param name="Vertices">Контурные точки cutter-полигона.</param>
/// <param name="Thickness">Толщина cutter (PL{T} профиль) — обычно MaxW+200 для thickness-cut или MaxT+200 для width-cut, плюс запас 200 мм.</param>
/// <param name="Name">Имя cutter-плиты для Part.Name (используется в drawings/reports как helper).</param>
/// <param name="TargetPartIndex">Индекс в списке flange-плит, к которой применить BooleanCut.</param>
public readonly record struct CutterPolygon(
    Vec3[] Vertices,
    double Thickness,
    string Name,
    int TargetPartIndex);
