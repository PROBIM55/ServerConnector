#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Structura.Tekla.Fachwerk;

/// <summary>
/// Pure geometry contract for the longitudinal weld preparation of a
/// fachwerk-column web. The caller supplies the centre axis of one already-
/// split physical web piece; both real longitudinal edge paths are derived
/// analytically, without BRep edge search.
/// </summary>
internal static class FachwerkColumnBevelGeometry
{
    internal const double NominalRemoval = 12.0;
    internal const double BevelAngleDeg = 45.0;
    internal const double CutterLeg = 14.0;
    // A later rigel fitting can extend an oblique web end by almost 500 mm.
    // Keep another full fitting allowance beyond that shifted end so the
    // triangular cutter crosses the complete final edge instead of ending
    // inside the web on the most inclined columns.
    internal const double DefaultEndOverrun = 1000.0;
    internal const double LowerCutterRotationOffset = 90.0;
    internal const double UpperCutterRotationOffset = 270.0;
    internal const string CutterProfile = "TRI_A14*14";
    internal const string CutterProfileA = "TRI_A14*14.0";
    internal const string CutterProfileB = "TRI_A14.0*14";
    internal const int TeklaPartNameLimit = 21;
    internal const int CutterNamePrefixLength = 4;

    private const double PointTolerance = 0.01;
    private const double Epsilon = 1e-7;

    private static FachwerkBevelSpec Build(
        string componentSemanticKey,
        string wallRole,
        string fragmentSemanticKey,
        string edgeRole,
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> externalEdge,
        string cutterProfile,
        double externalSurfaceLocalZ,
        FachwerkBevelPlane cutterPlane,
        FachwerkBevelDepth cutterDepth,
        FachwerkBevelRotation cutterRotation,
        double cutterRotationOffset,
        double endOverrun = DefaultEndOverrun,
        double cutterPlaneOffset = -1.0,
        double cutterDepthOffset = -1.0)
    {
        ValidateSemanticToken(componentSemanticKey, nameof(componentSemanticKey));
        ValidateWebRole(wallRole);
        ValidateSemanticToken(fragmentSemanticKey, nameof(fragmentSemanticKey));
        ValidateSemanticToken(edgeRole, nameof(edgeRole));
        ValidateSemanticToken(cutterProfile, nameof(cutterProfile));
        ValidateFinite(externalSurfaceLocalZ, nameof(externalSurfaceLocalZ));
        ValidateFinite(cutterRotationOffset, nameof(cutterRotationOffset));
        ValidateFinite(cutterPlaneOffset, nameof(cutterPlaneOffset));
        ValidateFinite(cutterDepthOffset, nameof(cutterDepthOffset));
        if (!IsFinite(endOverrun) || endOverrun <= Epsilon)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endOverrun),
                "Технологический выход режущей детали должен быть положительным.");
        }

        List<FachwerkColumnPrimitiveDefinition> sourcePath = CloneAndValidatePath(
            externalEdge,
            wallRole);
        List<FachwerkColumnPrimitiveDefinition> cutterPath = ClonePath(sourcePath);
        ExtendPathEnds(cutterPath, endOverrun);

        string normalizedRole = NormalizeToken(wallRole);
        string semanticKey = string.Concat(
            "fachwerk-column/bevel/",
            NormalizeToken(componentSemanticKey),
            "/",
            normalizedRole,
            "/",
            NormalizeToken(fragmentSemanticKey),
            "/",
            NormalizeToken(edgeRole));

        return new FachwerkBevelSpec(
            semanticKey,
            normalizedRole,
            fragmentSemanticKey.Trim(),
            NormalizeToken(edgeRole),
            sourcePath,
            cutterPath,
            cutterProfile.Trim(),
            externalSurfaceLocalZ,
            cutterPlane,
            cutterDepth,
            cutterRotation,
            cutterRotationOffset,
            cutterPlaneOffset,
            cutterDepthOffset,
            endOverrun);
    }

    /// <summary>
    /// Builds both longitudinal preparations of one physical web fragment.
    /// The source trajectory is the web centre axis; the two physical edge
    /// paths are exact constant offsets by half of the PL profile height.
    /// </summary>
    internal static IReadOnlyList<FachwerkBevelSpec> BuildForWebPieceEdges(
        string componentSemanticKey,
        FachwerkColumnPartSpec webPiece,
        string fragmentSemanticKey,
        double endOverrun = DefaultEndOverrun)
    {
        if (webPiece == null)
        {
            throw new ArgumentNullException(nameof(webPiece));
        }
        if (webPiece.Kind != FachwerkColumnPartKind.Web)
        {
            throw new ArgumentException(
                "Разделка стойки разрешена только для физического фрагмента стенки.",
                nameof(webPiece));
        }

        string role = NormalizeToken(webPiece.Role);
        ValidateWebRole(role);

        List<FachwerkColumnPrimitiveDefinition> directedAxis =
            CloneAndValidatePath(webPiece.FirstBoundary, role);
        EnsureTopToBottom(directedAxis);
        double webHeight = FachwerkColumnGeometry.ParsePlateHeight(webPiece.Profile);
        bool isUpperWeb;
        if (Math.Abs(webHeight - 280.0) <= PointTolerance)
        {
            isUpperWeb = true;
        }
        else if (Math.Abs(webHeight - 350.0) <= PointTolerance)
        {
            isUpperWeb = false;
        }
        else
        {
            throw new InvalidOperationException(
                "Для разделки стенки поддерживаются только профили PL25*350 и PL25*280; " +
                "получен профиль '" + webPiece.Profile + "'.");
        }

        double halfWebHeight = webHeight * 0.5;
        List<FachwerkColumnPrimitiveDefinition> negativeEdge =
            OffsetPath(directedAxis, -halfWebHeight, role + "-negative-edge");
        List<FachwerkColumnPrimitiveDefinition> positiveEdge =
            OffsetPath(directedAxis, halfWebHeight, role + "-positive-edge");

        // CreateWeb places the web center at local Z = -NormalOffset. The two
        // web roles face away from the box centre in opposite directions.
        double outwardSign = string.Equals(role, "inner-web", StringComparison.Ordinal)
            ? 1.0
            : -1.0;
        double externalSurfaceLocalZ =
            -webPiece.NormalOffset + outwardSign * FachwerkColumnGeometry.WebThickness * 0.5;
        double cutterRotationOffset = isUpperWeb
            ? UpperCutterRotationOffset
            : LowerCutterRotationOffset;

        if (string.Equals(role, "outer-web", StringComparison.Ordinal))
        {
            return new[]
            {
                Build(
                    componentSemanticKey,
                    role,
                    fragmentSemanticKey,
                    "inner-flange-edge",
                    negativeEdge,
                    CutterProfileA,
                    externalSurfaceLocalZ,
                    isUpperWeb ? FachwerkBevelPlane.Right : FachwerkBevelPlane.Left,
                    isUpperWeb ? FachwerkBevelDepth.Front : FachwerkBevelDepth.Behind,
                    isUpperWeb ? FachwerkBevelRotation.Front : FachwerkBevelRotation.Back,
                    cutterRotationOffset,
                    endOverrun),
                Build(
                    componentSemanticKey,
                    role,
                    fragmentSemanticKey,
                    "outer-flange-edge",
                    positiveEdge,
                    CutterProfileB,
                    externalSurfaceLocalZ,
                    isUpperWeb ? FachwerkBevelPlane.Right : FachwerkBevelPlane.Left,
                    isUpperWeb ? FachwerkBevelDepth.Behind : FachwerkBevelDepth.Front,
                    isUpperWeb ? FachwerkBevelRotation.Top : FachwerkBevelRotation.Below,
                    cutterRotationOffset,
                    endOverrun),
            };
        }

        return new[]
        {
            Build(
                componentSemanticKey,
                role,
                fragmentSemanticKey,
                "inner-flange-edge",
                negativeEdge,
                CutterProfileB,
                externalSurfaceLocalZ,
                isUpperWeb ? FachwerkBevelPlane.Left : FachwerkBevelPlane.Right,
                isUpperWeb ? FachwerkBevelDepth.Front : FachwerkBevelDepth.Behind,
                isUpperWeb ? FachwerkBevelRotation.Below : FachwerkBevelRotation.Top,
                cutterRotationOffset,
                endOverrun),
            Build(
                componentSemanticKey,
                role,
                fragmentSemanticKey,
                "outer-flange-edge",
                positiveEdge,
                CutterProfileA,
                externalSurfaceLocalZ,
                isUpperWeb ? FachwerkBevelPlane.Left : FachwerkBevelPlane.Right,
                isUpperWeb ? FachwerkBevelDepth.Behind : FachwerkBevelDepth.Front,
                isUpperWeb ? FachwerkBevelRotation.Back : FachwerkBevelRotation.Front,
                cutterRotationOffset,
                endOverrun),
        };
    }

    internal static string ComputeGeometrySignature(FachwerkBevelSpec spec)
    {
        if (spec == null)
        {
            throw new ArgumentNullException(nameof(spec));
        }

        StringBuilder value = new StringBuilder();
        value.Append(spec.SemanticKey).Append('|')
            .Append(spec.CutterProfile).Append('|')
            .Append(Format(NominalRemoval)).Append('|')
            .Append(Format(BevelAngleDeg)).Append('|')
            .Append(Format(CutterLeg)).Append('|')
            .Append(Format(spec.ExternalSurfaceLocalZ)).Append('|')
            .Append(spec.CutterPlane).Append('|')
            .Append(spec.CutterDepth).Append('|')
            .Append(spec.CutterRotation).Append('|')
            .Append(Format(spec.CutterRotationOffset)).Append('|')
            .Append(Format(spec.CutterPlaneOffset)).Append('|')
            .Append(Format(spec.CutterDepthOffset)).Append('|')
            .Append(Format(spec.EndOverrun));

        foreach (FachwerkColumnPrimitiveDefinition primitive in spec.CutterPath)
        {
            value.Append('|').Append(primitive.NormalizedKind())
                .Append(':').Append(Format(primitive.Start.X))
                .Append(',').Append(Format(primitive.Start.Y))
                .Append('>').Append(Format(primitive.End.X))
                .Append(',').Append(Format(primitive.End.Y));
            if (primitive.NormalizedKind() == "arc")
            {
                value.Append('@').Append(Format(primitive.Center.X))
                    .Append(',').Append(Format(primitive.Center.Y))
                    .Append(',').Append(Format(primitive.SweepDeg));
            }
        }

        return Hash(value.ToString(), 12);
    }

    private static List<FachwerkColumnPrimitiveDefinition> OffsetPath(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> source,
        double signedDistance,
        string role)
    {
        List<FachwerkColumnPrimitiveDefinition> result =
            new List<FachwerkColumnPrimitiveDefinition>(source.Count);
        foreach (FachwerkColumnPrimitiveDefinition primitive in source)
        {
            result.Add(FachwerkColumnGeometry.OffsetPrimitive(primitive, signedDistance));
        }
        FachwerkColumnGeometry.NormalizePrimitiveConnections(result, role);
        return result;
    }

    private static void EnsureTopToBottom(IList<FachwerkColumnPrimitiveDefinition> path)
    {
        if (path.Count == 0 ||
            path[0].Start.Y >= path[path.Count - 1].End.Y - PointTolerance)
        {
            return;
        }

        List<FachwerkColumnPrimitiveDefinition> reversed =
            new List<FachwerkColumnPrimitiveDefinition>(path.Count);
        for (int index = path.Count - 1; index >= 0; index--)
        {
            FachwerkColumnPrimitiveDefinition source = path[index];
            reversed.Add(new FachwerkColumnPrimitiveDefinition
            {
                Kind = source.NormalizedKind(),
                Start = source.End.Clone(),
                End = source.Start.Clone(),
                Center = source.Center?.Clone(),
                SweepDeg = -source.SweepDeg,
            });
        }

        path.Clear();
        foreach (FachwerkColumnPrimitiveDefinition primitive in reversed)
        {
            path.Add(primitive);
        }
    }

    private static List<FachwerkColumnPrimitiveDefinition> CloneAndValidatePath(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> source,
        string role)
    {
        if (source == null || source.Count == 0)
        {
            throw new ArgumentException(
                "У оси физического фрагмента стенки нет примитивов.",
                nameof(source));
        }

        List<FachwerkColumnPrimitiveDefinition> result = ClonePath(source);
        for (int index = 0; index < result.Count; index++)
        {
            FachwerkColumnPrimitiveDefinition primitive = result[index];
            primitive.Validate("bevel", role);
            ValidatePrimitiveLength(primitive);
            if (index == 0)
            {
                continue;
            }

            FachwerkColumnLocalPoint previousEnd = result[index - 1].End;
            double gap = Distance(previousEnd, primitive.Start);
            if (gap > PointTolerance)
            {
                throw new InvalidOperationException(
                    "Аналитическая внешняя кромка стенки разорвана на " +
                    gap.ToString("0.###", CultureInfo.InvariantCulture) + " мм.");
            }

            // Use one exact shared node without changing either primitive's
            // analytical type, centre or sweep.
            primitive.Start = previousEnd.Clone();
        }
        return result;
    }

    private static List<FachwerkColumnPrimitiveDefinition> ClonePath(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> source)
    {
        List<FachwerkColumnPrimitiveDefinition> result =
            new List<FachwerkColumnPrimitiveDefinition>(source.Count);
        foreach (FachwerkColumnPrimitiveDefinition primitive in source)
        {
            if (primitive == null)
            {
                throw new ArgumentException("В цепочке разделки найден пустой примитив.", nameof(source));
            }
            result.Add(new FachwerkColumnPrimitiveDefinition
            {
                Kind = primitive.NormalizedKind(),
                Start = primitive.Start?.Clone(),
                End = primitive.End?.Clone(),
                Center = primitive.Center?.Clone(),
                SweepDeg = primitive.SweepDeg,
            });
        }
        return result;
    }

    private static void ExtendPathEnds(
        IList<FachwerkColumnPrimitiveDefinition> primitives,
        double distance)
    {
        ExtendPrimitiveStart(primitives[0], distance);
        ExtendPrimitiveEnd(primitives[primitives.Count - 1], distance);
    }

    private static void ExtendPrimitiveStart(
        FachwerkColumnPrimitiveDefinition primitive,
        double distance)
    {
        if (primitive.NormalizedKind() == "line")
        {
            double dx = primitive.End.X - primitive.Start.X;
            double dy = primitive.End.Y - primitive.Start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            primitive.Start = NewPoint(
                primitive.Start.X - distance * dx / length,
                primitive.Start.Y - distance * dy / length);
            return;
        }

        double radius = ArcRadius(primitive);
        double direction = Math.Sign(primitive.SweepDeg);
        double extensionAngle = distance / radius;
        double startAngle = Math.Atan2(
            primitive.Start.Y - primitive.Center.Y,
            primitive.Start.X - primitive.Center.X);
        primitive.Start = PointOnCircle(
            primitive.Center,
            radius,
            startAngle - direction * extensionAngle);
        primitive.SweepDeg += direction * extensionAngle * 180.0 / Math.PI;
    }

    private static void ExtendPrimitiveEnd(
        FachwerkColumnPrimitiveDefinition primitive,
        double distance)
    {
        if (primitive.NormalizedKind() == "line")
        {
            double dx = primitive.End.X - primitive.Start.X;
            double dy = primitive.End.Y - primitive.Start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            primitive.End = NewPoint(
                primitive.End.X + distance * dx / length,
                primitive.End.Y + distance * dy / length);
            return;
        }

        double radius = ArcRadius(primitive);
        double direction = Math.Sign(primitive.SweepDeg);
        double extensionAngle = distance / radius;
        double endAngle = Math.Atan2(
            primitive.End.Y - primitive.Center.Y,
            primitive.End.X - primitive.Center.X);
        primitive.End = PointOnCircle(
            primitive.Center,
            radius,
            endAngle + direction * extensionAngle);
        primitive.SweepDeg += direction * extensionAngle * 180.0 / Math.PI;
    }

    private static void ValidatePrimitiveLength(FachwerkColumnPrimitiveDefinition primitive)
    {
        if (primitive.NormalizedKind() == "line")
        {
            if (Distance(primitive.Start, primitive.End) <= Epsilon)
            {
                throw new InvalidOperationException("В цепочке разделки найдена вырожденная линия.");
            }
            return;
        }

        if (ArcRadius(primitive) <= Epsilon)
        {
            throw new InvalidOperationException("В цепочке разделки найдена вырожденная дуга.");
        }
    }

    private static double ArcRadius(FachwerkColumnPrimitiveDefinition primitive)
    {
        return Distance(primitive.Start, primitive.Center);
    }

    private static FachwerkColumnLocalPoint PointOnCircle(
        FachwerkColumnLocalPoint center,
        double radius,
        double angle)
    {
        return NewPoint(
            center.X + radius * Math.Cos(angle),
            center.Y + radius * Math.Sin(angle));
    }

    private static FachwerkColumnLocalPoint NewPoint(double x, double y)
    {
        return new FachwerkColumnLocalPoint { X = x, Y = y };
    }

    private static double Distance(
        FachwerkColumnLocalPoint first,
        FachwerkColumnLocalPoint second)
    {
        double dx = second.X - first.X;
        double dy = second.Y - first.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void ValidateWebRole(string role)
    {
        string normalized = NormalizeToken(role);
        if (normalized != "inner-web" && normalized != "outer-web")
        {
            throw new ArgumentException(
                "Продольная разделка разрешена только для ролей inner-web и outer-web.",
                nameof(role));
        }
    }

    private static void ValidateSemanticToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Семантический ключ не может быть пустым.", parameterName);
        }
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Координата должна быть конечным числом.");
        }
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static string NormalizeToken(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string Format(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    internal static string Hash(string value, int byteCount)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            StringBuilder result = new StringBuilder(byteCount * 2);
            for (int index = 0; index < Math.Min(byteCount, bytes.Length); index++)
            {
                result.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
            }
            return result.ToString();
        }
    }
}

internal enum FachwerkBevelPlane
{
    Left,
    Right,
}

internal enum FachwerkBevelDepth
{
    Front,
    Behind,
}

internal enum FachwerkBevelRotation
{
    Front,
    Back,
    Top,
    Below,
}

internal sealed class FachwerkBevelSpec
{
    internal FachwerkBevelSpec(
        string semanticKey,
        string wallRole,
        string fragmentSemanticKey,
        string edgeRole,
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> externalEdge,
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> cutterPath,
        string cutterProfile,
        double externalSurfaceLocalZ,
        FachwerkBevelPlane cutterPlane,
        FachwerkBevelDepth cutterDepth,
        FachwerkBevelRotation cutterRotation,
        double cutterRotationOffset,
        double cutterPlaneOffset,
        double cutterDepthOffset,
        double endOverrun)
    {
        SemanticKey = semanticKey;
        StableId = FachwerkColumnBevelGeometry.Hash(semanticKey, 10);
        WallRole = wallRole;
        FragmentSemanticKey = fragmentSemanticKey;
        EdgeRole = edgeRole;
        ExternalEdge = externalEdge;
        CutterPath = cutterPath;
        CutterProfile = cutterProfile;
        ExternalSurfaceLocalZ = externalSurfaceLocalZ;
        CutterPlane = cutterPlane;
        CutterDepth = cutterDepth;
        CutterRotation = cutterRotation;
        CutterRotationOffset = cutterRotationOffset;
        CutterPlaneOffset = cutterPlaneOffset;
        CutterDepthOffset = cutterDepthOffset;
        EndOverrun = endOverrun;
        GeometrySignature = FachwerkColumnBevelGeometry.ComputeGeometrySignature(this);
    }

    public string SemanticKey { get; }
    public string StableId { get; }
    public string WallRole { get; }
    public string FragmentSemanticKey { get; }
    public string EdgeRole { get; }
    public IReadOnlyList<FachwerkColumnPrimitiveDefinition> ExternalEdge { get; }
    public IReadOnlyList<FachwerkColumnPrimitiveDefinition> CutterPath { get; }
    public string CutterProfile { get; }
    public double ExternalSurfaceLocalZ { get; }
    public FachwerkBevelPlane CutterPlane { get; }
    public FachwerkBevelDepth CutterDepth { get; }
    public FachwerkBevelRotation CutterRotation { get; }
    public double CutterRotationOffset { get; }
    public double CutterPlaneOffset { get; }
    public double CutterDepthOffset { get; }
    public double EndOverrun { get; }
    public string GeometrySignature { get; }

    public double NominalRemoval => FachwerkColumnBevelGeometry.NominalRemoval;
    public double BevelAngleDeg => FachwerkColumnBevelGeometry.BevelAngleDeg;
    public double CutterLeg => FachwerkColumnBevelGeometry.CutterLeg;
    // Tekla 2020 truncates Part.Name to 21 characters. Keep the complete
    // semantic identity inside that limit so an existing Boolean cut can be
    // found and replaced after a component modification.
    public string CutterNamePrefix => "FKB_" + StableId.Substring(
        0,
        Math.Min(
            StableId.Length,
            FachwerkColumnBevelGeometry.TeklaPartNameLimit -
                FachwerkColumnBevelGeometry.CutterNamePrefixLength));
    public string CutterName => CutterNamePrefix;
}
