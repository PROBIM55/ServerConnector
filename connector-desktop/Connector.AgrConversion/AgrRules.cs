namespace Connector.AgrConversion;

/// <summary>
/// Правила разбора части АГР, общие с эталонами B1 (Blender) и спайком S2 (Assimp).
/// </summary>
public static class AgrRules
{
    /// <summary>Оси результата: как в эталоне B1 и в МСК-77 относительно точки geojson.</summary>
    public const string FrameDescription =
        "x — восток, y — север, z — вверх (оси B1, МСК-77 от точки geojson), метры; трансформации узлов запечены";

    /// <summary>Перевод осей assimp в оси результата, запись как в S2.</summary>
    public const string AssimpToAgrAxes = "x,-z,y";

    /// <summary>
    /// assimp 6.0.2 переводит FBX АГР (Z вверх, UnitScaleFactor 100) в Y вверх корневой матрицей; обратно в оси
    /// B1: (x, −z, y). Масштаб 1 — FBX уже в метрах. Поворот собственный (определитель +1), обход граней сохраняется.
    /// </summary>
    public static void ToAgrFrame(double ax, double ay, double az, out double x, out double y, out double z)
    {
        x = ax;
        y = -az;
        z = ay;
    }

    /// <summary>UDIM-тайл грани по центроиду UV: 1001 + floor(u) + 10·floor(v) (UV как в FBX, v вверх).</summary>
    public static int TileOfFace(float[] uv, int i0, int i1, int i2)
    {
        double cu = ((double)uv[i0 * 2] + uv[i1 * 2] + uv[i2 * 2]) / 3.0;
        double cv = ((double)uv[i0 * 2 + 1] + uv[i1 * 2 + 1] + uv[i2 * 2 + 1]) / 3.0;
        return TileOf(cu, cv);
    }

    public static int TileOf(double u, double v) => 1001 + (int)Math.Floor(u) + 10 * (int)Math.Floor(v);

    /// <summary>Однотонная заглушка: разброс каждого канала RGB по всем пикселям не больше 2/255 (правило B1).</summary>
    public const double UniformSpan255 = 2.0;

    /// <summary>Коллизии Unreal (convex/box/capsule/sphere): в модель не входят, считаются в «отброшено».</summary>
    public static readonly string[] CollisionPrefixes = { "UCX_", "UBX_", "UCP_", "USP_" };

    public static bool IsCollisionName(string name)
    {
        foreach (var p in CollisionPrefixes)
        {
            if (name.StartsWith(p, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Суффикс копии Blender (".001") в имени материала — для сверки с эталоном.</summary>
    public static string NormalizeMaterialName(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot > 0 && name.Length - dot == 4 && name.AsSpan(dot + 1).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return name[..dot];
        }
        return name;
    }
}
