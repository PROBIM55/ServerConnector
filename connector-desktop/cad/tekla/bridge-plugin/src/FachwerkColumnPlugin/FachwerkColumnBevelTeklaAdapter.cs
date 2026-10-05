#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Structura.Tekla.Fachwerk;

/// <summary>
/// Tekla 2020 adapter for an idempotent web-bevel BooleanPart. A replacement
/// cut is inserted before the previous component output is removed, and
/// duplicate cuts with the same semantic identity are deleted.
/// </summary>
internal static class FachwerkColumnBevelTeklaAdapter
{
    private const string BevelIdProperty = "FK_BEVEL_ID";
    private const string BevelKeyProperty = "FK_BEVEL_KEY";
    private const string BevelSignatureProperty = "FK_BEVEL_SIG";
    private const string BevelRoleProperty = "FK_ROLE";

    internal static FachwerkBevelApplyResult Replace(
        Model model,
        Part father,
        FachwerkColumnFrame frame,
        FachwerkBevelSpec spec,
        string fallbackMaterial = "S355")
    {
        if (model == null)
        {
            throw new ArgumentNullException(nameof(model));
        }
        if (father == null || father.Identifier == null || father.Identifier.ID == 0)
        {
            throw new ArgumentException("Стенка для разделки должна существовать в модели Tekla.", nameof(father));
        }
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }
        if (spec == null)
        {
            throw new ArgumentNullException(nameof(spec));
        }

        List<BooleanPart> existing = FindMatchingCuts(father, spec);
        Part cutter = null;
        BooleanPart created = null;
        try
        {
            // The caller creates both the physical web and its Boolean output
            // in the same facade plane. Re-applying a plane built from the
            // component-local frame here makes Tekla transform the cutter a
            // second time and sends it away from its father part.
            cutter = CreateCutter(spec, FirstNonEmpty(
                father.Material?.MaterialString,
                fallbackMaterial,
                "S355"));
            if (!cutter.Insert())
            {
                throw new InvalidOperationException(
                    "Tekla не создала режущую деталь разделки '" + spec.SemanticKey + "'.");
            }

            // Tekla 2020 maps plugin BooleanPart output by insertion order.
            // Remove the previous semantic output before inserting its replacement.
            int removed = DeleteAll(existing);
            created = new BooleanPart
            {
                Father = father,
                Type = BooleanPart.BooleanTypeEnum.BOOLEAN_CUT,
            };
            created.SetOperativePart(cutter);
            if (!created.Insert())
            {
                throw new InvalidOperationException(
                    "Tekla не создала BooleanPart разделки '" + spec.SemanticKey + "'.");
            }

            try
            {
                cutter.Delete();
            }
            catch
            {
                // The BooleanPart already owns a copy of the cutter shape.
                // A failed cleanup must not invalidate the successfully made cut.
            }
            cutter = null;
            return FachwerkBevelApplyResult.Created(created, removed);
        }
        catch
        {
            if (created != null && created.Identifier != null && created.Identifier.ID != 0)
            {
                try { created.Delete(); } catch { }
            }
            if (cutter != null && cutter.Identifier != null && cutter.Identifier.ID != 0)
            {
                try { cutter.Delete(); } catch { }
            }
            throw;
        }
    }

    internal static int Delete(Model model, Part father, FachwerkBevelSpec spec)
    {
        if (model == null)
        {
            throw new ArgumentNullException(nameof(model));
        }
        if (father == null)
        {
            throw new ArgumentNullException(nameof(father));
        }
        if (spec == null)
        {
            throw new ArgumentNullException(nameof(spec));
        }
        return DeleteAll(FindMatchingCuts(father, spec));
    }

    private static Part CreateCutter(FachwerkBevelSpec spec, string material)
    {
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> path = spec.CutterPath;
        Part cutter;
        if (path.Count == 1 && path[0].NormalizedKind() == "line")
        {
            cutter = new Beam(
                ToPoint(path[0].Start, spec.ExternalSurfaceLocalZ),
                ToPoint(path[0].End, spec.ExternalSurfaceLocalZ));
        }
        else
        {
            IReadOnlyList<FachwerkColumnContourVertex> vertices =
                FachwerkColumnGeometry.BuildContourVertices(path);
            if (vertices.Count < 2)
            {
                throw new InvalidOperationException(
                    "Аналитическая траектория режущей детали содержит меньше двух вершин.");
            }

            PolyBeam polyBeam = new PolyBeam(PolyBeam.PolyBeamTypeEnum.BEAM);
            foreach (FachwerkColumnContourVertex vertex in vertices)
            {
                if (!polyBeam.AddContourPoint(new ContourPoint(
                        ToPoint(vertex.Point, spec.ExternalSurfaceLocalZ),
                        CreateChamfer(vertex))))
                {
                    throw new InvalidOperationException(
                        "Tekla отклонила аналитическую вершину режущей детали разделки.");
                }
            }
            cutter = polyBeam;
        }

        cutter.Name = spec.CutterName;
        cutter.Class = BooleanPart.BooleanOperativeClassName;
        cutter.Profile.ProfileString = spec.CutterProfile;
        cutter.Material.MaterialString = material;
        cutter.Position.Plane = ToPlane(spec.CutterPlane);
        cutter.Position.PlaneOffset = spec.CutterPlaneOffset;
        cutter.Position.Depth = ToDepth(spec.CutterDepth);
        cutter.Position.DepthOffset = spec.CutterDepthOffset;
        cutter.Position.Rotation = ToRotation(spec.CutterRotation);
        cutter.Position.RotationOffset = spec.CutterRotationOffset;
        TrySetUserProperty(cutter, BevelIdProperty, spec.StableId);
        TrySetUserProperty(cutter, BevelKeyProperty, spec.SemanticKey);
        TrySetUserProperty(cutter, BevelSignatureProperty, spec.GeometrySignature);
        TrySetUserProperty(cutter, BevelRoleProperty, "web-bevel-" + spec.EdgeRole);
        return cutter;
    }

    private static Position.PlaneEnum ToPlane(FachwerkBevelPlane value)
    {
        switch (value)
        {
            case FachwerkBevelPlane.Left:
                return Position.PlaneEnum.LEFT;
            case FachwerkBevelPlane.Right:
                return Position.PlaneEnum.RIGHT;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, "Неизвестная плоскость разделки.");
        }
    }

    private static Position.DepthEnum ToDepth(FachwerkBevelDepth value)
    {
        switch (value)
        {
            case FachwerkBevelDepth.Front:
                return Position.DepthEnum.FRONT;
            case FachwerkBevelDepth.Behind:
                return Position.DepthEnum.BEHIND;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, "Неизвестная глубина разделки.");
        }
    }

    private static Position.RotationEnum ToRotation(FachwerkBevelRotation value)
    {
        switch (value)
        {
            case FachwerkBevelRotation.Front:
                return Position.RotationEnum.FRONT;
            case FachwerkBevelRotation.Back:
                return Position.RotationEnum.BACK;
            case FachwerkBevelRotation.Top:
                return Position.RotationEnum.TOP;
            case FachwerkBevelRotation.Below:
                return Position.RotationEnum.BELOW;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, "Неизвестный поворот разделки.");
        }
    }

    private static void TrySetUserProperty(ModelObject modelObject, string name, string value)
    {
        try
        {
            modelObject.SetUserProperty(name, value ?? string.Empty);
        }
        catch
        {
            // The operative-part name is the canonical fallback identity on
            // Tekla environments where these optional UDAs are not registered.
        }
    }

    private static List<BooleanPart> FindMatchingCuts(Part father, FachwerkBevelSpec spec)
    {
        List<BooleanPart> result = new List<BooleanPart>();
        ModelObjectEnumerator enumerator = father.GetBooleans();
        while (enumerator.MoveNext())
        {
            if (!(enumerator.Current is BooleanPart candidate))
            {
                continue;
            }

            if (Matches(candidate, spec))
            {
                result.Add(candidate);
            }
        }
        return result;
    }

    private static bool Matches(BooleanPart candidate, FachwerkBevelSpec spec)
    {
        string storedId = null;
        try { candidate.GetUserProperty(BevelIdProperty, ref storedId); } catch { }
        if (string.Equals(storedId, spec.StableId, StringComparison.Ordinal))
        {
            return true;
        }

        string cutterName = TryGetCutterName(candidate);
        return !string.IsNullOrWhiteSpace(cutterName) &&
            cutterName.StartsWith(spec.CutterNamePrefix, StringComparison.Ordinal);
    }

    private static BooleanPart FindCurrentCut(
        IReadOnlyList<BooleanPart> candidates,
        FachwerkBevelSpec spec)
    {
        foreach (BooleanPart candidate in candidates)
        {
            string cutterName = TryGetCutterName(candidate);
            if (string.Equals(cutterName, spec.CutterName, StringComparison.Ordinal))
            {
                return candidate;
            }

            string storedSignature = null;
            try { candidate.GetUserProperty(BevelSignatureProperty, ref storedSignature); } catch { }
            if (string.Equals(storedSignature, spec.GeometrySignature, StringComparison.Ordinal))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string TryGetCutterName(BooleanPart candidate)
    {
        try
        {
            Part operativePart = candidate.OperativePart;
            if (operativePart == null)
            {
                return null;
            }
            try { operativePart.Select(); } catch { }
            return operativePart.Name;
        }
        catch
        {
            return null;
        }
    }

    private static int DeleteAllExcept(
        IReadOnlyList<BooleanPart> values,
        BooleanPart keep)
    {
        int removed = 0;
        foreach (BooleanPart value in values)
        {
            if (value.Identifier != null && keep.Identifier != null &&
                value.Identifier.ID == keep.Identifier.ID)
            {
                continue;
            }
            if (!value.Delete())
            {
                throw new InvalidOperationException(
                    "Tekla не удалила дублирующую операцию разделки ID " +
                    (value.Identifier?.ID ?? 0).ToString(CultureInfo.InvariantCulture) + ".");
            }
            removed++;
        }
        return removed;
    }

    private static int DeleteAll(IReadOnlyList<BooleanPart> values)
    {
        int removed = 0;
        foreach (BooleanPart value in values)
        {
            if (!value.Delete())
            {
                throw new InvalidOperationException(
                    "Tekla не заменила прежнюю операцию разделки ID " +
                    (value.Identifier?.ID ?? 0).ToString(CultureInfo.InvariantCulture) + ".");
            }
            removed++;
        }
        return removed;
    }

    private static Point ToPoint(FachwerkColumnLocalPoint point, double localZ)
    {
        return new Point(point.X, point.Y, localZ);
    }

    private static Chamfer CreateChamfer(FachwerkColumnContourVertex vertex)
    {
        switch (vertex.ChamferKind)
        {
            case FachwerkColumnChamferKind.Rounding:
                return new Chamfer(
                    vertex.Radius,
                    0.0,
                    Chamfer.ChamferTypeEnum.CHAMFER_ROUNDING);
            case FachwerkColumnChamferKind.ArcPoint:
                return new Chamfer(
                    0.0,
                    0.0,
                    Chamfer.ChamferTypeEnum.CHAMFER_ARC_POINT);
            default:
                return new Chamfer(
                    0.0,
                    0.0,
                    Chamfer.ChamferTypeEnum.CHAMFER_NONE);
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        if (values == null)
        {
            return null;
        }
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }
}

internal sealed class FachwerkBevelApplyResult
{
    private FachwerkBevelApplyResult(
        BooleanPart booleanPart,
        bool created,
        int removedDuplicateCount)
    {
        BooleanPart = booleanPart;
        WasCreated = created;
        RemovedDuplicateCount = removedDuplicateCount;
    }

    public BooleanPart BooleanPart { get; }
    public bool WasCreated { get; }
    public bool WasReused => !WasCreated;
    public int RemovedDuplicateCount { get; }

    internal static FachwerkBevelApplyResult Created(
        BooleanPart booleanPart,
        int removedDuplicateCount)
    {
        return new FachwerkBevelApplyResult(booleanPart, true, removedDuplicateCount);
    }

    internal static FachwerkBevelApplyResult Reused(
        BooleanPart booleanPart,
        int removedDuplicateCount)
    {
        return new FachwerkBevelApplyResult(booleanPart, false, removedDuplicateCount);
    }
}
