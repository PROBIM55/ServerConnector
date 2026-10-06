using Platform.Bridge.Geometry.Geometry3d;

namespace Platform.Bridge.Geometry.Placement;

/// <summary>
/// Описание одной физической плиты пояса в мировых координатах.
/// Pure-DTO — на этом уровне ничего не знает про Tekla. Phase E
/// конвертирует <see cref="PlateSpec"/> → ContourPlate через TeklaShim.
/// </summary>
/// <param name="Start">Точка центра плиты на стартовой грани (s = DetailBoundary.Start).</param>
/// <param name="End">Точка центра плиты на конечной грани (s = DetailBoundary.End).</param>
/// <param name="ExtrudeAxis">Поперечная ось плиты (= BeamFrame.Ey, единичный вектор).</param>
/// <param name="NormalAxis">Вертикальная ось плиты (= BeamFrame.Ez, единичный вектор).</param>
/// <param name="Width">Ширина плиты (вдоль ExtrudeAxis), мм.</param>
/// <param name="Thickness">Толщина плиты (вдоль NormalAxis), мм.</param>
/// <param name="Material">Tekla material string (S355, S420 и т.д.).</param>
/// <param name="Name">Имя для Part.Name — обычно "&lt;bridgeName&gt;_FLG_NN".</param>
/// <param name="ClassId">Class-string для Part.Class. Для нижнего/верхнего пояса оба = "3".</param>
public readonly record struct PlateSpec(
    Vec3 Start,
    Vec3 End,
    Vec3 ExtrudeAxis,
    Vec3 NormalAxis,
    double Width,
    double Thickness,
    string Material,
    string Name,
    string ClassId);
