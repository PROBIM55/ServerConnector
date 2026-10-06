#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Structura.Tekla.Fachwerk;

internal sealed class FachwerkColumnErectionJointApplyResult
{
    internal FachwerkColumnErectionJointApplyResult(
        IReadOnlyList<ModelObject> createdObjects,
        int insertCount,
        int bevelCount)
    {
        CreatedObjects = createdObjects;
        InsertCount = insertCount;
        BevelCount = bevelCount;
    }

    public IReadOnlyList<ModelObject> CreatedObjects { get; }
    public int InsertCount { get; }
    public int BevelCount { get; }
}

internal static class FachwerkColumnErectionJointTeklaAdapter
{
    private const double CutterOverrun = 5.0;
    private const double CutterCrossOverrun = 20.0;
    private const string InsertPrefix = "515-60.ВС-";
    private const string InsertClass = "7";

    internal static FachwerkColumnErectionJointApplyResult Apply(
        string mark,
        string material,
        string className,
        int componentId,
        IReadOnlyList<FachwerkColumnErectionJointSpec> joints,
        IReadOnlyList<Part> physicalParts)
    {
        if (joints == null) throw new ArgumentNullException(nameof(joints));
        if (physicalParts == null)
            throw new ArgumentNullException(nameof(physicalParts));
        if (joints.Count == 0)
        {
            return new FachwerkColumnErectionJointApplyResult(
                Array.Empty<ModelObject>(),
                0,
                0);
        }

        var created = new List<ModelObject>();
        var inserts = new Dictionary<string, ContourPlate>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var joint in joints)
            {
                foreach (var spec in joint.Inserts)
                {
                    var insert = CreateInsert(
                        mark,
                        material,
                        className,
                        componentId,
                        spec);
                    created.Add(insert);
                    inserts.Add(InsertKey(
                        spec.BreakIndex,
                        spec.FlangeRole), insert);
                }
            }

            var bevelCount = 0;
            foreach (var joint in joints)
            {
                foreach (var bevel in joint.Bevels)
                {
                    var target = ResolveTarget(
                        bevel,
                        inserts,
                        physicalParts);
                    var booleanPart = CreateBevel(
                        target,
                        material,
                        componentId,
                        bevel);
                    created.Add(booleanPart);
                    bevelCount++;
                }
            }

            return new FachwerkColumnErectionJointApplyResult(
                new ReadOnlyCollection<ModelObject>(created),
                inserts.Count,
                bevelCount);
        }
        catch
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                try { created[index].Delete(); }
                catch { }
            }
            throw;
        }
    }

    private static ContourPlate CreateInsert(
        string mark,
        string material,
        string className,
        int componentId,
        FachwerkColumnErectionInsertSpec spec)
    {
        var halfWidth = FachwerkColumnGeometry.FlangeWidth * 0.5;
        var insertName = GetInsertName(spec.FlangeRole);
        var plate = new ContourPlate
        {
            Name = insertName,
            Class = InsertClass,
            PartNumber = new NumberingSeries(
                InsertPrefix,
                1),
        };
        plate.Profile.ProfileString =
            "PL" + FachwerkColumnGeometry.FlangeThickness.ToString(
                "0.###",
                CultureInfo.InvariantCulture);
        plate.Material.MaterialString = string.IsNullOrWhiteSpace(material)
            ? "C355-5"
            : material.Trim();
        plate.Position.Depth = Position.DepthEnum.MIDDLE;

        AddContourPoint(plate, spec.Start, -halfWidth);
        AddContourPoint(plate, spec.End, -halfWidth);
        AddContourPoint(plate, spec.End, halfWidth);
        AddContourPoint(plate, spec.Start, halfWidth);
        if (!plate.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала вставку " + spec.FlangeRole +
                " монтажного стыка " +
                spec.BreakIndex.ToString(CultureInfo.InvariantCulture) + ".");
        }

        TrySetUserProperty(plate, "FK_ROLE", spec.SemanticRole);
        TrySetUserProperty(plate, "FK_MARK", mark ?? string.Empty);
        TrySetUserProperty(
            plate,
            "FK_OWNER",
            componentId.ToString(CultureInfo.InvariantCulture));
        TrySetUserProperty(
            plate,
            "FK_BREAK",
            spec.BreakIndex.ToString(CultureInfo.InvariantCulture));
        if (!plate.Modify())
        {
            plate.Delete();
            throw new InvalidOperationException(
                "Tekla не применила атрибуты детали вставки монтажного стыка.");
        }

        var assembly = plate.GetAssembly();
        if (assembly == null)
        {
            plate.Delete();
            throw new InvalidOperationException(
                "Tekla не вернула сборку вставки монтажного стыка.");
        }
        assembly.Name = insertName;
        assembly.AssemblyNumber = new NumberingSeries(InsertPrefix, 1);
        if (!assembly.Modify())
        {
            plate.Delete();
            throw new InvalidOperationException(
                "Tekla не применила атрибуты сборки вставки монтажного стыка.");
        }
        return plate;
    }

    private static string GetInsertName(string flangeRole)
    {
        if (string.Equals(flangeRole, "inner-flange", StringComparison.Ordinal))
        {
            return "Внутренняя вставка";
        }
        if (string.Equals(flangeRole, "outer-flange", StringComparison.Ordinal))
        {
            return "Наружная вставка";
        }

        throw new ArgumentOutOfRangeException(
            nameof(flangeRole),
            flangeRole,
            "Неизвестная роль вставки монтажного стыка.");
    }

    private static void AddContourPoint(
        ContourPlate plate,
        FachwerkColumnLocalPoint point,
        double localZ)
    {
        if (!plate.AddContourPoint(new ContourPoint(
            new Point(point.X, point.Y, localZ),
            new Chamfer())))
        {
            throw new InvalidOperationException(
                "Tekla отклонила вершину вставки монтажного стыка.");
        }
    }

    private static Part ResolveTarget(
        FachwerkColumnErectionBevelSpec bevel,
        IReadOnlyDictionary<string, ContourPlate> inserts,
        IReadOnlyList<Part> physicalParts)
    {
        if (bevel.Target ==
            FachwerkColumnErectionBevelTarget.FlangeInsert)
        {
            if (!inserts.TryGetValue(
                InsertKey(bevel.BreakIndex, bevel.FlangeInsertRole),
                out var insert))
            {
                throw new InvalidOperationException(
                    "Для разделки не найдена вставка " +
                    bevel.FlangeInsertRole + " монтажного стыка " +
                    bevel.BreakIndex.ToString(
                        CultureInfo.InvariantCulture) + ".");
            }
            return insert;
        }

        if (bevel.PhysicalPartIndex < 0 ||
            bevel.PhysicalPartIndex >= physicalParts.Count)
        {
            throw new InvalidOperationException(
                "Для разделки '" + bevel.SemanticRole +
                "' не найдена физическая деталь стойки.");
        }
        return physicalParts[bevel.PhysicalPartIndex];
    }

    private static BooleanPart CreateBevel(
        Part target,
        string fallbackMaterial,
        int componentId,
        FachwerkColumnErectionBevelSpec spec)
    {
        if (target == null ||
            target.Identifier == null ||
            target.Identifier.ID == 0)
        {
            throw new InvalidOperationException(
                "Целевая деталь разделки монтажного стыка не существует.");
        }

        var cutter = BuildCutter(target, fallbackMaterial, componentId, spec);
        BooleanPart booleanPart = null;
        try
        {
            if (!cutter.Insert())
            {
                throw new InvalidOperationException(
                    "Tekla не создала режущий клин '" +
                    spec.SemanticRole + "'.");
            }
            booleanPart = new BooleanPart
            {
                Father = target,
                Type = BooleanPart.BooleanTypeEnum.BOOLEAN_CUT,
            };
            booleanPart.SetOperativePart(cutter);
            if (!booleanPart.Insert())
            {
                throw new InvalidOperationException(
                    "Tekla не создала разделку '" +
                    spec.SemanticRole + "'.");
            }
            TrySetUserProperty(
                booleanPart,
                "FK_ROLE",
                "erection-bevel");
            TrySetUserProperty(
                booleanPart,
                "FK_BREAK",
                spec.BreakIndex.ToString(CultureInfo.InvariantCulture));
            TrySetUserProperty(
                booleanPart,
                "FK_OWNER",
                componentId.ToString(CultureInfo.InvariantCulture));
            booleanPart.Modify();
            try { cutter.Delete(); }
            catch { }
            return booleanPart;
        }
        catch
        {
            if (booleanPart != null &&
                booleanPart.Identifier != null &&
                booleanPart.Identifier.ID != 0)
            {
                try { booleanPart.Delete(); }
                catch { }
            }
            if (cutter.Identifier != null && cutter.Identifier.ID != 0)
            {
                try { cutter.Delete(); }
                catch { }
            }
            throw;
        }
    }

    private static ContourPlate BuildCutter(
        Part target,
        string fallbackMaterial,
        int componentId,
        FachwerkColumnErectionBevelSpec spec)
    {
        var tangent = Normalize(spec.UpwardTangent, "касательная разделки");
        var outward = OrthogonalizedOutward(
            spec.OutwardNormal,
            tangent);
        var halfThickness = spec.Thickness * 0.5;
        var rootCoordinate =
            -halfThickness + FachwerkColumnGeometry.ErectionBevelRootFace;
        var outerCoordinate = halfThickness;
        var run =
            (spec.Thickness -
             FachwerkColumnGeometry.ErectionBevelRootFace) /
            Math.Tan(
                (90.0 - FachwerkColumnGeometry.ErectionBevelAngleDeg) *
                Math.PI / 180.0);
        var anchor = new Vector(
            spec.Anchor.X,
            spec.Anchor.Y,
            spec.AnchorZ);

        var contour = new[]
        {
            PointAt(
                anchor,
                tangent,
                -CutterOverrun,
                outward,
                rootCoordinate),
            PointAt(
                anchor,
                tangent,
                -CutterOverrun,
                outward,
                outerCoordinate + CutterOverrun),
            PointAt(
                anchor,
                tangent,
                run + CutterOverrun,
                outward,
                outerCoordinate + CutterOverrun),
            PointAt(
                anchor,
                tangent,
                run,
                outward,
                outerCoordinate),
            PointAt(
                anchor,
                tangent,
                0,
                outward,
                rootCoordinate),
        };

        var cutter = new ContourPlate
        {
            Name = BuildCutterName(spec),
            Class = BooleanPart.BooleanOperativeClassName,
        };
        cutter.Profile.ProfileString =
            "PL" + (spec.CrossSpan + 2 * CutterCrossOverrun).ToString(
                "0.###",
                CultureInfo.InvariantCulture);
        cutter.Material.MaterialString = FirstNonEmpty(
            target.Material?.MaterialString,
            fallbackMaterial,
            "C355-5");
        cutter.Position.Depth = Position.DepthEnum.MIDDLE;
        foreach (var point in contour)
        {
            if (!cutter.AddContourPoint(new ContourPoint(
                point,
                new Chamfer())))
            {
                throw new InvalidOperationException(
                    "Tekla отклонила вершину режущего клина '" +
                    spec.SemanticRole + "'.");
            }
        }
        TrySetUserProperty(cutter, "FK_ROLE", "erection-bevel-cutter");
        TrySetUserProperty(
            cutter,
            "FK_OWNER",
            componentId.ToString(CultureInfo.InvariantCulture));
        return cutter;
    }

    private static Vector Normalize(FachwerkVector value, string label)
    {
        var length = Math.Sqrt(
            value.X * value.X +
            value.Y * value.Y +
            value.Z * value.Z);
        if (length <= 1e-9)
        {
            throw new InvalidOperationException(
                "Нулевой вектор: " + label + ".");
        }
        return new Vector(
            value.X / length,
            value.Y / length,
            value.Z / length);
    }

    private static Vector OrthogonalizedOutward(
        FachwerkVector source,
        Vector tangent)
    {
        var raw = Normalize(source, "наружная нормаль разделки");
        var dot =
            raw.X * tangent.X +
            raw.Y * tangent.Y +
            raw.Z * tangent.Z;
        var orthogonal = new FachwerkVector(
            raw.X - dot * tangent.X,
            raw.Y - dot * tangent.Y,
            raw.Z - dot * tangent.Z);
        return Normalize(orthogonal, "наружная нормаль разделки");
    }

    private static Point PointAt(
        Vector anchor,
        Vector tangent,
        double tangentDistance,
        Vector outward,
        double outwardDistance) =>
        new Point(
            anchor.X +
            tangent.X * tangentDistance +
            outward.X * outwardDistance,
            anchor.Y +
            tangent.Y * tangentDistance +
            outward.Y * outwardDistance,
            anchor.Z +
            tangent.Z * tangentDistance +
            outward.Z * outwardDistance);

    private static string InsertKey(int breakIndex, string flangeRole) =>
        breakIndex.ToString(CultureInfo.InvariantCulture) + "/" +
        (flangeRole ?? string.Empty);

    private static string BuildCutterName(
        FachwerkColumnErectionBevelSpec spec)
    {
        var role = spec.SemanticRole
            .Replace("inner-", "I-")
            .Replace("outer-", "O-")
            .Replace("flange", "F")
            .Replace("web", "W")
            .Replace("erection-insert", "INS")
            .Replace("-upper-lower-end", "-UP")
            .Replace("-lower", "-LO");
        var value =
            "FKMJ" +
            spec.BreakIndex.ToString(CultureInfo.InvariantCulture) +
            "-" + role;
        return value.Length <= 21 ? value : value.Substring(0, 21);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        return null;
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
            // Optional diagnostic UDA may be absent in a Tekla environment.
        }
    }
}
