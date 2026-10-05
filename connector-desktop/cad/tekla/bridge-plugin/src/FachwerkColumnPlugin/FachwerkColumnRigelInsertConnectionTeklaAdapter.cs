#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Structura.Tekla.Fachwerk;

internal enum FachwerkRigelInsertDepthMode
{
    Auto,
    Front,
    Middle,
    Behind,
}

internal static class FachwerkColumnRigelInsertConnectionTeklaAdapter
{
    internal const string InsertPrefix = "515-60.ВС-";
    internal const string Material = "C355-5";
    internal const string InsertClass = "7";
    internal static readonly Position.DepthEnum InsertDepth =
        Position.DepthEnum.MIDDLE;

    internal static ContourPlate CreateInsert(
        FachwerkRigelInsertPlateSpec spec,
        FachwerkRigelInsertDepthMode depthMode,
        int componentId)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        if (spec.Boundary == null || spec.Boundary.Count != 4)
        {
            throw new InvalidOperationException(
                "Контур вставки '" + spec.OutputRole +
                "' должен содержать четыре точки.");
        }

        var plate = new ContourPlate
        {
            Name = spec.Name,
            Class = InsertClass,
            Finish = spec.Template?.Finish ?? string.Empty,
        };
        plate.Profile.ProfileString =
            "PL" + spec.Thickness.ToString("0.###", CultureInfo.InvariantCulture);
        plate.Material.MaterialString = Material;
        ResolveInsertDepth(
            spec.Boundary,
            spec.Outward,
            spec.Thickness,
            depthMode,
            out Position.DepthEnum depth,
            out double depthOffset);
        plate.Position.Depth = depth;
        plate.Position.DepthOffset = depthOffset;
        plate.PartNumber = new NumberingSeries(InsertPrefix, 1);
        plate.AssemblyNumber = new NumberingSeries(InsertPrefix, 1);
        foreach (Point point in spec.Boundary)
        {
            if (!plate.AddContourPoint(
                    new ContourPoint(new Point(point), new Chamfer())))
            {
                throw new InvalidOperationException(
                    "Tekla отклонила вершину вставки '" + spec.OutputRole + "'.");
            }
        }
        Tag(plate, spec.OutputRole, spec.SemanticRole, spec.SectionRole, componentId);
        if (!plate.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала вставку '" + spec.Name + "'.");
        }

        Assembly assembly = plate.GetAssembly();
        if (assembly == null)
        {
            plate.Delete();
            throw new InvalidOperationException(
                "Tekla не вернула сборку вставки '" + spec.Name + "'.");
        }
        assembly.Name = spec.Name;
        assembly.AssemblyNumber = new NumberingSeries(InsertPrefix, 1);
        TrySetUserProperty(assembly, "FK_ROLE", spec.OutputRole);
        TrySetUserProperty(
            assembly,
            "FK_OWNER",
            componentId.ToString(CultureInfo.InvariantCulture));
        if (!assembly.Modify())
        {
            plate.Delete();
            throw new InvalidOperationException(
                "Tekla не сохранила свойства сборки вставки '" + spec.Name + "'.");
        }
        return plate;
    }

    internal static FachwerkRigelInsertDepthMode NormalizeDepthMode(string value)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? "AUTO"
            : value.Trim().ToUpperInvariant();
        switch (normalized)
        {
            case "AUTO": return FachwerkRigelInsertDepthMode.Auto;
            case "FRONT": return FachwerkRigelInsertDepthMode.Front;
            case "MIDDLE": return FachwerkRigelInsertDepthMode.Middle;
            case "BEHIND": return FachwerkRigelInsertDepthMode.Behind;
            default:
                throw new InvalidOperationException(
                    "Выравнивание вставки должно быть AUTO, FRONT, MIDDLE или BEHIND.");
        }
    }

    internal static void ResolveInsertDepth(
        IReadOnlyList<Point> boundary,
        Vector outward,
        double thickness,
        FachwerkRigelInsertDepthMode mode,
        out Position.DepthEnum depth,
        out double offset)
    {
        offset = 0.0;
        switch (mode)
        {
            case FachwerkRigelInsertDepthMode.Auto:
                depth = InsertDepth;
                offset = ResolveInsertDepthOffset(boundary, outward, thickness);
                return;
            case FachwerkRigelInsertDepthMode.Front:
                depth = Position.DepthEnum.FRONT;
                return;
            case FachwerkRigelInsertDepthMode.Middle:
                depth = Position.DepthEnum.MIDDLE;
                return;
            case FachwerkRigelInsertDepthMode.Behind:
                depth = Position.DepthEnum.BEHIND;
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    internal static double ResolveInsertDepthOffset(
        IReadOnlyList<Point> boundary,
        Vector outward,
        double thickness)
    {
        if (boundary == null || boundary.Count < 3)
            throw new ArgumentException("Контур вставки не задан.", nameof(boundary));
        if (outward == null)
            throw new ArgumentNullException(nameof(outward));
        if (thickness <= 0)
            throw new ArgumentOutOfRangeException(nameof(thickness));

        Vector contourNormal = BoundaryNormal(boundary);
        double orientation = Dot(contourNormal, Normalize(outward));
        if (Math.Abs(orientation) < 0.95)
        {
            throw new InvalidOperationException(
                "Контур вставки не лежит во внутренней плоскости детали.");
        }

        return Math.Sign(orientation) * thickness * 0.5;
    }

    private static Vector BoundaryNormal(IReadOnlyList<Point> boundary)
    {
        var normal = new Vector();
        for (var index = 0; index < boundary.Count; index++)
        {
            Point current = boundary[index];
            Point next = boundary[(index + 1) % boundary.Count];
            normal.X += (current.Y - next.Y) * (current.Z + next.Z);
            normal.Y += (current.Z - next.Z) * (current.X + next.X);
            normal.Z += (current.X - next.X) * (current.Y + next.Y);
        }
        return Normalize(normal);
    }

    private static Vector Normalize(Vector vector)
    {
        double length = Math.Sqrt(Dot(vector, vector));
        if (length <= 1e-9)
            throw new InvalidOperationException("Контур вставки имеет нулевую нормаль.");
        return new Vector(
            vector.X / length,
            vector.Y / length,
            vector.Z / length);
    }

    private static double Dot(Vector first, Vector second) =>
        first.X * second.X + first.Y * second.Y + first.Z * second.Z;

    private static void Tag(
        ModelObject modelObject,
        string outputRole,
        string semanticRole,
        string sectionRole,
        int componentId)
    {
        TrySetUserProperty(modelObject, "FK_ROLE", outputRole);
        TrySetUserProperty(modelObject, "FK_SOURCE_ROLE", semanticRole);
        TrySetUserProperty(modelObject, "FK_SECTION", sectionRole);
        TrySetUserProperty(
            modelObject,
            "FK_OWNER",
            componentId.ToString(CultureInfo.InvariantCulture));
    }

    private static void TrySetUserProperty(
        ModelObject modelObject,
        string name,
        string value)
    {
        try
        {
            modelObject.SetUserProperty(name, value ?? string.Empty);
        }
        catch
        {
            // Optional semantic tags must not block geometry on older models.
        }
    }
}
