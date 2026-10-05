namespace Platform.Cad.AutoCad.Adapter.Sessions;

/// <summary>
/// Pure-конвертация entity-спецификаций в массивы double для late-bound COM вызовов
/// AutoCAD (double[] маршалится как SAFEARRAY(VT_R8) — формат, который принимают
/// ModelSpace.AddLine/AddPolyline/AddCircle/AddText). Без COM-зависимостей — под unit-тесты.
/// </summary>
public static class AutoCadEntityVariantConverter
{
    public static double[] ToPoint(double x, double y, double z) => [x, y, z];

    public static double[] LineStart(AutoCadLineSpec line) => ToPoint(line.X1, line.Y1, line.Z1);

    public static double[] LineEnd(AutoCadLineSpec line) => ToPoint(line.X2, line.Y2, line.Z2);

    public static double[] CircleCenter(AutoCadCircleSpec circle) => ToPoint(circle.Cx, circle.Cy, circle.Cz);

    public static double[] TextInsertionPoint(AutoCadTextSpec text) => ToPoint(text.X, text.Y, text.Z);

    /// <summary>
    /// Полилиния → плоский массив [x1,y1,z1, x2,y2,z2, ...] для AddPolyline.
    /// Точки [x,y] дополняются z=0. Меньше 2 точек или неверная размерность — ArgumentException.
    /// </summary>
    public static double[] PolylineFlat3d(AutoCadPolylineSpec polyline)
    {
        if (polyline.Points.Count < 2)
        {
            throw new ArgumentException("Polyline requires at least 2 points.", nameof(polyline));
        }

        var flat = new double[polyline.Points.Count * 3];
        for (var i = 0; i < polyline.Points.Count; i++)
        {
            var point = polyline.Points[i];
            if (point.Count is not (2 or 3))
            {
                throw new ArgumentException($"Polyline point #{i} must have 2 or 3 coordinates, got {point.Count}.", nameof(polyline));
            }

            flat[i * 3] = point[0];
            flat[i * 3 + 1] = point[1];
            flat[i * 3 + 2] = point.Count == 3 ? point[2] : 0d;
        }

        return flat;
    }
}
