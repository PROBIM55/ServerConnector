#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace Structura.Tekla.Fachwerk;

internal enum FachwerkColumnStiffenerPlacementMode
{
    Elevations,
    Spacing,
    Automatic,
}

internal sealed class FachwerkColumnStiffenerSettings
{
    internal FachwerkColumnStiffenerSettings(
        bool enabled,
        string profile,
        string material,
        double chamferSize,
        IReadOnlyList<double> elevations)
        : this(
            enabled,
            profile,
            material,
            chamferSize,
            FachwerkColumnStiffenerPlacementMode.Elevations,
            1500,
            20,
            elevations)
    {
    }

    internal FachwerkColumnStiffenerSettings(
        bool enabled,
        string profile,
        string material,
        double chamferSize,
        FachwerkColumnStiffenerPlacementMode placementMode,
        double spacing,
        double innerFlangeGap,
        IReadOnlyList<double> elevations)
        : this(
            enabled,
            profile,
            material,
            chamferSize,
            placementMode,
            new[] { spacing },
            innerFlangeGap,
            elevations)
    {
    }

    internal FachwerkColumnStiffenerSettings(
        bool enabled,
        string profile,
        string material,
        double chamferSize,
        FachwerkColumnStiffenerPlacementMode placementMode,
        IReadOnlyList<double> incrementalDistances,
        double innerFlangeGap,
        IReadOnlyList<double> elevations)
        : this(
            enabled,
            profile,
            material,
            chamferSize,
            placementMode,
            incrementalDistances,
            innerFlangeGap,
            false,
            elevations)
    {
    }

    internal FachwerkColumnStiffenerSettings(
        bool enabled,
        string profile,
        string material,
        double chamferSize,
        FachwerkColumnStiffenerPlacementMode placementMode,
        IReadOnlyList<double> incrementalDistances,
        double innerFlangeGap,
        bool alternateFlangeGap,
        IReadOnlyList<double> elevations)
    {
        Enabled = enabled;
        Profile = profile;
        Material = material;
        ChamferSize = chamferSize;
        PlacementMode = placementMode;
        IncrementalDistances = incrementalDistances ??
            Array.Empty<double>();
        InnerFlangeGap = innerFlangeGap;
        AlternateFlangeGap = alternateFlangeGap;
        Elevations = elevations;
    }

    public bool Enabled { get; }
    public string Profile { get; }
    public string Material { get; }
    public double ChamferSize { get; }
    public FachwerkColumnStiffenerPlacementMode PlacementMode { get; }
    public IReadOnlyList<double> IncrementalDistances { get; }
    public double InnerFlangeGap { get; }
    public bool AlternateFlangeGap { get; }
    public IReadOnlyList<double> Elevations { get; }
}

internal sealed class FachwerkColumnStiffenerPlacement
{
    internal FachwerkColumnStiffenerPlacement(
        double elevation,
        double? outerPathLocation)
    {
        Elevation = elevation;
        OuterPathLocation = outerPathLocation;
    }

    public double Elevation { get; }
    public double? OuterPathLocation { get; }
}

internal sealed class FachwerkColumnStiffenerPoint
{
    internal FachwerkColumnStiffenerPoint(
        double x,
        double y,
        double z,
        bool hasChamfer)
    {
        X = x;
        Y = y;
        Z = z;
        HasChamfer = hasChamfer;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }
    public bool HasChamfer { get; }
}

internal sealed class FachwerkColumnStiffenerSpec
{
    internal FachwerkColumnStiffenerSpec(
        int index,
        double elevation,
        string profile,
        string material,
        double chamferSize,
        int assemblySegmentIndex,
        bool gapAtOuterFlange,
        FachwerkColumnLocalPoint outerAxisAnchor,
        FachwerkVector planeNormal,
        IReadOnlyList<FachwerkColumnStiffenerPoint> contour)
    {
        Index = index;
        Elevation = elevation;
        Profile = profile;
        Material = material;
        ChamferSize = chamferSize;
        AssemblySegmentIndex = assemblySegmentIndex;
        GapAtOuterFlange = gapAtOuterFlange;
        OuterAxisAnchor = outerAxisAnchor;
        PlaneNormal = planeNormal;
        Contour = contour;
    }

    public int Index { get; }
    public double Elevation { get; }
    public string Profile { get; }
    public string Material { get; }
    public double ChamferSize { get; }
    public int AssemblySegmentIndex { get; }
    public bool GapAtOuterFlange { get; }
    public FachwerkColumnLocalPoint OuterAxisAnchor { get; }
    public FachwerkVector PlaneNormal { get; }
    public IReadOnlyList<FachwerkColumnStiffenerPoint> Contour { get; }
}

internal static partial class FachwerkColumnGeometry
{
    private const double StiffenerTolerance = 0.25;
    private const double DefaultStiffenerChamfer = 20.0;
    private const double DefaultStiffenerInnerGap = 20.0;
    private const double WebInnerFaceOffset =
        WebNormalOffset - WebThickness * 0.5;

    internal static FachwerkColumnStiffenerSettings ReadStiffenerSettings(
        FachwerkColumnPluginData data)
    {
        if (data == null || !IsEnabledValue(data.StiffenerEnabled))
        {
            return new FachwerkColumnStiffenerSettings(
                false,
                "PL8",
                "C355-5",
                DefaultStiffenerChamfer,
                FachwerkColumnStiffenerPlacementMode.Elevations,
                Array.Empty<double>(),
                DefaultStiffenerInnerGap,
                Array.Empty<double>());
        }

        var profile = NormalizeRequiredText(
            data.StiffenerProfile,
            "PL8",
            "Профиль внутренних рёбер");
        var material = NormalizeRequiredText(
            data.StiffenerMaterial,
            "C355-5",
            "Материал внутренних рёбер");
        var chamfer = NormalizeStiffenerChamfer(data.StiffenerChamfer);
        var placementMode = NormalizeStiffenerPlacementMode(
            data.StiffenerPlacementMode);
        var incrementalDistances = placementMode ==
            FachwerkColumnStiffenerPlacementMode.Spacing
            ? ParseStiffenerDistances(
                data.StiffenerDistances,
                data.StiffenerSpacing)
            : Array.Empty<double>();
        if (placementMode == FachwerkColumnStiffenerPlacementMode.Automatic)
        {
            incrementalDistances = new[]
            {
                NormalizePositiveStiffenerSpacing(data.StiffenerSpacing),
            };
        }
        var innerFlangeGap = NormalizeNonNegativeStiffenerValue(
            data.StiffenerInnerFlangeGap,
            DefaultStiffenerInnerGap,
            "Зазор внутреннего ребра от внутреннего пояса");
        var elevations = ParseStiffenerElevations(data.StiffenerElevations);
        var alternateFlangeGap = IsEnabledValue(
            data.StiffenerAlternateFlangeGap);
        return new FachwerkColumnStiffenerSettings(
            true,
            profile,
            material,
            chamfer,
            placementMode,
            incrementalDistances,
            innerFlangeGap,
            alternateFlangeGap,
            elevations);
    }

    internal static IReadOnlyList<double> ParseStiffenerDistances(
        string serialized,
        double legacySpacing = 0)
    {
        if (string.Equals(
                (serialized ?? string.Empty).Trim(),
                int.MinValue.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            serialized = string.Empty;
        }
        if (string.IsNullOrWhiteSpace(serialized))
        {
            if (legacySpacing > StiffenerTolerance &&
                !double.IsNaN(legacySpacing) &&
                !double.IsInfinity(legacySpacing))
            {
                return new ReadOnlyCollection<double>(
                    new List<double> { legacySpacing });
            }
            return Array.Empty<double>();
        }

        var values = serialized.Split(
            new[] { ' ', '\t', '\r', '\n', ';' },
            StringSplitOptions.RemoveEmptyEntries);
        var result = new List<double>(values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var raw = values[index].Trim();
            if (!TryParseStiffenerDouble(raw, out var distance) ||
                double.IsNaN(distance) ||
                double.IsInfinity(distance) ||
                distance <= StiffenerTolerance)
            {
                throw new InvalidOperationException(
                    "Расстояние внутреннего ребра '" + raw +
                    "' должно быть положительным конечным числом. " +
                    "Разделяйте последовательные расстояния пробелом или ';'.");
            }
            result.Add(distance);
        }
        return new ReadOnlyCollection<double>(result);
    }

    internal static IReadOnlyList<double> ParseStiffenerElevations(
        string serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized) ||
            string.Equals(
                serialized.Trim(),
                int.MinValue.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return Array.Empty<double>();
        }

        var values = serialized.Split(
            new[] { ';', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        var parsed = new List<double>(values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var raw = values[index].Trim();
            if (!TryParseStiffenerDouble(raw, out var elevation) ||
                double.IsNaN(elevation) ||
                double.IsInfinity(elevation) ||
                elevation <= -1_000_000)
            {
                throw new InvalidOperationException(
                    "Отметка внутреннего ребра '" + raw +
                    "' должна содержать конечное число. Разделяйте отметки символом ';'.");
            }
            parsed.Add(elevation);
        }

        parsed.Sort();
        for (var index = 1; index < parsed.Count; index++)
        {
            if (Math.Abs(parsed[index] - parsed[index - 1]) <= StiffenerTolerance)
            {
                throw new InvalidOperationException(
                    "Отметки внутренних рёбер " +
                    parsed[index - 1].ToString("0.###", CultureInfo.InvariantCulture) +
                    " и " +
                    parsed[index].ToString("0.###", CultureInfo.InvariantCulture) +
                    " совпадают с учётом допуска.");
            }
        }
        return new ReadOnlyCollection<double>(parsed);
    }

    internal static IReadOnlyList<FachwerkColumnStiffenerSpec> BuildStiffenerSpecs(
        FachwerkColumnProfileDefinition profile,
        FachwerkColumnFrame frame,
        IReadOnlyList<FachwerkColumnBreak> breaks,
        FachwerkColumnStiffenerSettings settings)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (!settings.Enabled)
        {
            return Array.Empty<FachwerkColumnStiffenerSpec>();
        }

        profile.Validate();
        var sourceInner = profile.RequirePath("inner-flange");
        var sourceOuter = profile.RequirePath("outer-flange");
        var physicalInner = OffsetToward(
            sourceInner,
            sourceOuter,
            FlangeThickness * 0.5,
            "inner-flange",
            "PL50*180",
            0);
        var physicalOuter = OffsetToward(
            sourceOuter,
            sourceInner,
            FlangeThickness * 0.5,
            "outer-flange",
            "PL50*180",
            0);
        var transitionBreaks = BuildEffectiveBreaks(
                profile,
                frame,
                Array.Empty<FachwerkColumnBreak>())
            .Where(item => item.IsSectionTransition())
            .ToArray();
        var physicalInnerPrimitives = BuildTransitionExtendedPieces(
                physicalInner,
                profile.SectionTransition,
                frame,
                transitionBreaks)
            .SelectMany(item => item.Primitives)
            .ToArray();
        var effectiveSettings = settings;
        if (settings.PlacementMode ==
            FachwerkColumnStiffenerPlacementMode.Automatic)
        {
            var automaticDistances =
                CalculateAutomaticStiffenerDistances(
                    profile,
                    frame,
                    breaks,
                    settings.IncrementalDistances.Single());
            effectiveSettings = new FachwerkColumnStiffenerSettings(
                settings.Enabled,
                settings.Profile,
                settings.Material,
                settings.ChamferSize,
                FachwerkColumnStiffenerPlacementMode.Spacing,
                automaticDistances,
                settings.InnerFlangeGap,
                settings.AlternateFlangeGap,
                settings.Elevations);
        }
        var placements = ResolveStiffenerPlacements(
            frame,
            sourceOuter.Primitives,
            effectiveSettings);
        if (placements.Count == 0)
        {
            return Array.Empty<FachwerkColumnStiffenerSpec>();
        }
        var effectiveUserBreaks = (breaks ?? Array.Empty<FachwerkColumnBreak>())
            .Where(item =>
                item != null &&
                !item.IsSectionTransition() &&
                !item.IsStiffenerReference() &&
                !IsStrictlyInsideSectionTransition(
                    item.Elevation - frame.GlobalInsertionZ,
                    profile.SectionTransition))
            .OrderBy(item => item.Elevation)
            .ThenBy(item => item.Index)
            .ToArray();

        var result = new List<FachwerkColumnStiffenerSpec>(
            placements.Count);
        for (var index = 0; index < placements.Count; index++)
        {
            var placement = placements[index];
            var elevation = placement.Elevation;
            var localY = elevation - frame.GlobalInsertionZ;

            var outerLocation = placement.OuterPathLocation ??
                RequireUniqueLocationAtY(
                    physicalOuter.Primitives,
                    localY,
                    "наружного пояса для внутреннего ребра",
                    index + 1);
            var outerAxis = PointAtPathLocation(
                physicalOuter.Primitives,
                outerLocation);
            var normal = UpwardTangentAtPathLocation(
                physicalOuter.Primitives,
                outerLocation);
            var referenceBreak = new FachwerkColumnBreak(
                elevation,
                FachwerkColumnBreak.AllFourMode,
                index + 1);
            var plane = new FachwerkColumnJointPlane(
                referenceBreak,
                0,
                outerAxis,
                normal,
                false);
            if (!TryFindNearestLocationAtPlane(
                physicalInnerPrimitives,
                plane,
                0,
                out var innerLocation))
            {
                throw new InvalidOperationException(
                    "Плоскость внутреннего ребра " +
                    elevation.ToString("0.###", CultureInfo.InvariantCulture) +
                    " не пересекла физическую ось внутреннего пояса.");
            }
            var innerAxis = PointAtPathLocation(
                physicalInnerPrimitives,
                innerLocation);
            var depthX = innerAxis.X - outerAxis.X;
            var depthY = innerAxis.Y - outerAxis.Y;
            var axisDistance = Math.Sqrt(depthX * depthX + depthY * depthY);
            var gapAtOuterFlange =
                settings.AlternateFlangeGap &&
                index % 2 == 0;
            var outerFlangeGap = gapAtOuterFlange
                ? settings.InnerFlangeGap
                : 0;
            var innerFlangeGap = gapAtOuterFlange
                ? 0
                : settings.InnerFlangeGap;
            if (axisDistance <=
                FlangeThickness +
                outerFlangeGap +
                innerFlangeGap +
                StiffenerTolerance)
            {
                throw new InvalidOperationException(
                    "На отметке внутреннего ребра " +
                    elevation.ToString("0.###", CultureInfo.InvariantCulture) +
                    " внутренние поверхности поясов и заданный зазор пересеклись.");
            }
            var inwardX = depthX / axisDistance;
            var inwardY = depthY / axisDistance;
            var outerFace = new FachwerkColumnLocalPoint
            {
                X = outerAxis.X +
                    inwardX * (FlangeThickness * 0.5 + outerFlangeGap),
                Y = outerAxis.Y +
                    inwardY * (FlangeThickness * 0.5 + outerFlangeGap),
            };
            var innerFace = new FachwerkColumnLocalPoint
            {
                X = innerAxis.X -
                    inwardX * (FlangeThickness * 0.5 + innerFlangeGap),
                Y = innerAxis.Y -
                    inwardY * (FlangeThickness * 0.5 + innerFlangeGap),
            };
            var contour = new[]
            {
                new FachwerkColumnStiffenerPoint(
                    outerFace.X,
                    outerFace.Y,
                    -WebInnerFaceOffset,
                    !gapAtOuterFlange),
                new FachwerkColumnStiffenerPoint(
                    innerFace.X,
                    innerFace.Y,
                    -WebInnerFaceOffset,
                    gapAtOuterFlange),
                new FachwerkColumnStiffenerPoint(
                    innerFace.X,
                    innerFace.Y,
                    WebInnerFaceOffset,
                    gapAtOuterFlange),
                new FachwerkColumnStiffenerPoint(
                    outerFace.X,
                    outerFace.Y,
                    WebInnerFaceOffset,
                    !gapAtOuterFlange),
            };
            ValidateStiffenerContour(contour, settings.ChamferSize, elevation);
            var assemblySegmentIndex = effectiveUserBreaks.Count(
                item => item.Elevation < elevation - StiffenerTolerance);
            result.Add(new FachwerkColumnStiffenerSpec(
                result.Count + 1,
                elevation,
                settings.Profile,
                settings.Material,
                settings.ChamferSize,
                assemblySegmentIndex,
                gapAtOuterFlange,
                outerAxis,
                normal,
                new ReadOnlyCollection<FachwerkColumnStiffenerPoint>(
                    contour)));
        }
        return new ReadOnlyCollection<FachwerkColumnStiffenerSpec>(result);
    }

    internal static IReadOnlyList<double>
        CalculateAutomaticStiffenerDistances(
            FachwerkColumnProfileDefinition profile,
            FachwerkColumnFrame frame,
            IReadOnlyList<FachwerkColumnBreak> breaks,
            double spacing)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        profile.Validate();
        spacing = NormalizePositiveStiffenerSpacing(spacing);

        var primitives = profile.RequirePath("outer-flange").Primitives;
        var totalLength = StiffenerPathLength(primitives);
        var anchors = new List<double>
        {
            StiffenerStationFromUpperEndAtY(
                primitives,
                (profile.SectionTransition.StartY +
                 profile.SectionTransition.EndY) * 0.5,
                "перехода сечения 450/380"),
        };
        foreach (var item in breaks ?? Array.Empty<FachwerkColumnBreak>())
        {
            if (item == null ||
                item.IsErectionSplice() ||
                item.IsSectionTransition())
            {
                continue;
            }
            var localY = item.Elevation - frame.GlobalInsertionZ;
            if (!item.IsStiffenerReference() &&
                IsStrictlyInsideSectionTransition(
                    localY,
                    profile.SectionTransition))
            {
                continue;
            }
            anchors.Add(StiffenerStationFromUpperEndAtY(
                primitives,
                localY,
                "стыка " + item.Index.ToString(
                    CultureInfo.InvariantCulture)));
        }
        return BuildAutomaticStiffenerDistances(
            totalLength,
            anchors,
            spacing);
    }

    internal static IReadOnlyList<double> BuildAutomaticStiffenerDistances(
        double totalLength,
        IEnumerable<double> anchorStationsFromUpperEnd,
        double spacing)
    {
        spacing = NormalizePositiveStiffenerSpacing(spacing);
        if (double.IsNaN(totalLength) ||
            double.IsInfinity(totalLength) ||
            totalLength <= StiffenerTolerance)
        {
            throw new InvalidOperationException(
                "Длина наружного пояса для авторасстановки рёбер должна быть положительной.");
        }

        var anchors = (anchorStationsFromUpperEnd ?? Array.Empty<double>())
            .Where(item =>
                !double.IsNaN(item) &&
                !double.IsInfinity(item) &&
                item > StiffenerTolerance &&
                item < totalLength - StiffenerTolerance)
            .OrderBy(item => item)
            .ToList();
        var uniqueAnchors = new List<double>(anchors.Count + 2) { 0 };
        foreach (var anchor in anchors)
        {
            if (Math.Abs(anchor - uniqueAnchors[uniqueAnchors.Count - 1]) >
                StiffenerTolerance)
            {
                uniqueAnchors.Add(anchor);
            }
        }
        uniqueAnchors.Add(totalLength);

        var stations = new List<double>();
        const double minimumEndGap = 1000.0;
        for (var segmentIndex = 1;
             segmentIndex < uniqueAnchors.Count;
             segmentIndex++)
        {
            var segmentStart = uniqueAnchors[segmentIndex - 1];
            var segmentEnd = uniqueAnchors[segmentIndex];
            var firstStationIndex = stations.Count;
            for (var station = segmentStart + spacing;
                 station < segmentEnd - StiffenerTolerance;
                 station += spacing)
            {
                stations.Add(station);
                if (stations.Count > 10000)
                {
                    throw new InvalidOperationException(
                        "Авторасстановка сформировала более 10000 внутренних рёбер.");
                }
            }

            if (stations.Count <= firstStationIndex)
            {
                continue;
            }
            var lastStationIndex = stations.Count - 1;
            var lastStation = stations[lastStationIndex];
            var endGap = segmentEnd - lastStation;
            if (endGap >= minimumEndGap - StiffenerTolerance)
            {
                continue;
            }
            var precedingStation = lastStationIndex > firstStationIndex
                ? stations[lastStationIndex - 1]
                : segmentStart;
            stations[lastStationIndex] =
                precedingStation + (segmentEnd - precedingStation) * 0.5;
        }

        var distances = new List<double>(stations.Count);
        var previousStation = 0.0;
        foreach (var station in stations)
        {
            distances.Add(station - previousStation);
            previousStation = station;
        }
        return new ReadOnlyCollection<double>(distances);
    }

    internal static string SerializeStiffenerDistances(
        IEnumerable<double> distances) =>
        string.Join(
            " ",
            (distances ?? Array.Empty<double>()).Select(item =>
                item.ToString("0.###", CultureInfo.InvariantCulture)));

    private static double StiffenerStationFromUpperEndAtY(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives,
        double localY,
        string label)
    {
        var location = RequireUniqueLocationAtY(
            primitives,
            localY,
            "наружного пояса для " + label,
            0);
        var distanceFromPathStart =
            StiffenerPathDistanceAtLocation(primitives, location);
        var totalLength = StiffenerPathLength(primitives);
        var pathStartsAtUpperEnd =
            primitives[0].Start.Y >=
            primitives[primitives.Count - 1].End.Y;
        return pathStartsAtUpperEnd
            ? distanceFromPathStart
            : totalLength - distanceFromPathStart;
    }

    private static double StiffenerPathDistanceAtLocation(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives,
        double location)
    {
        if (location <= 0) return 0;
        if (location >= primitives.Count)
            return StiffenerPathLength(primitives);
        var primitiveIndex = Math.Max(
            0,
            Math.Min(primitives.Count - 1, (int)Math.Floor(location)));
        var parameter = Math.Max(0, Math.Min(1, location - primitiveIndex));
        var result = 0.0;
        for (var index = 0; index < primitiveIndex; index++)
            result += StiffenerPrimitiveLength(primitives[index]);
        return result +
            StiffenerPrimitiveLength(primitives[primitiveIndex]) * parameter;
    }

    private static IReadOnlyList<FachwerkColumnStiffenerPlacement>
        ResolveStiffenerPlacements(
            FachwerkColumnFrame frame,
            IReadOnlyList<FachwerkColumnPrimitiveDefinition> outerAxisPrimitives,
            FachwerkColumnStiffenerSettings settings)
    {
        if (settings.PlacementMode ==
            FachwerkColumnStiffenerPlacementMode.Elevations)
        {
            return new ReadOnlyCollection<FachwerkColumnStiffenerPlacement>(
                settings.Elevations
                    .OrderByDescending(item => item)
                    .Select(item =>
                        new FachwerkColumnStiffenerPlacement(item, null))
                    .ToList());
        }

        var totalLength = StiffenerPathLength(outerAxisPrimitives);
        if (totalLength <= StiffenerTolerance ||
            settings.IncrementalDistances.Count == 0)
        {
            return Array.Empty<FachwerkColumnStiffenerPlacement>();
        }

        if (settings.IncrementalDistances.Count > 10000)
        {
            throw new InvalidOperationException(
                "Задано более 10000 расстояний внутренних рёбер.");
        }

        var pathStartsAtUpperEnd =
            outerAxisPrimitives[0].Start.Y >=
            outerAxisPrimitives[outerAxisPrimitives.Count - 1].End.Y;
        var placements = new List<FachwerkColumnStiffenerPlacement>(
            settings.IncrementalDistances.Count);
        var stationFromUpperEnd = 0.0;
        for (var index = 0;
             index < settings.IncrementalDistances.Count;
             index++)
        {
            stationFromUpperEnd += settings.IncrementalDistances[index];
            if (stationFromUpperEnd >= totalLength - StiffenerTolerance)
            {
                throw new InvalidOperationException(
                    "Суммарное расстояние до внутреннего ребра " +
                    (index + 1).ToString(CultureInfo.InvariantCulture) +
                    " равно " +
                    stationFromUpperEnd.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) +
                    " мм и выходит за длину наружного пояса " +
                    totalLength.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) +
                    " мм.");
            }
            var pathDistance = pathStartsAtUpperEnd
                ? stationFromUpperEnd
                : totalLength - stationFromUpperEnd;
            var location = StiffenerPathLocationAtDistance(
                outerAxisPrimitives,
                pathDistance);
            var point = PointAtPathLocation(outerAxisPrimitives, location);
            placements.Add(new FachwerkColumnStiffenerPlacement(
                frame.GlobalInsertionZ + point.Y,
                location));
        }
        return new ReadOnlyCollection<FachwerkColumnStiffenerPlacement>(
            placements);
    }

    private static double StiffenerPathLength(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives)
    {
        if (primitives == null || primitives.Count == 0)
        {
            throw new InvalidOperationException(
                "У наружного пояса отсутствует траектория для расстановки рёбер.");
        }
        return primitives.Sum(StiffenerPrimitiveLength);
    }

    private static double StiffenerPathLocationAtDistance(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives,
        double distance)
    {
        if (distance <= 0) return 0;
        var remaining = distance;
        for (var index = 0; index < primitives.Count; index++)
        {
            var length = StiffenerPrimitiveLength(primitives[index]);
            if (length <= StiffenerTolerance)
                continue;
            if (remaining <= length + StiffenerTolerance)
            {
                return index + Math.Max(0, Math.Min(1, remaining / length));
            }
            remaining -= length;
        }
        return primitives.Count;
    }

    private static double StiffenerPrimitiveLength(
        FachwerkColumnPrimitiveDefinition primitive)
    {
        if (primitive.NormalizedKind() == "line")
            return Distance(primitive.Start, primitive.End);
        var radius = Distance(primitive.Start, primitive.Center);
        return radius * Math.Abs(primitive.SweepDeg) * Math.PI / 180.0;
    }

    private static void ValidateStiffenerContour(
        IReadOnlyList<FachwerkColumnStiffenerPoint> contour,
        double chamfer,
        double elevation)
    {
        if (contour == null || contour.Count != 4)
        {
            throw new InvalidOperationException(
                "Контур внутреннего ребра должен содержать четыре вершины.");
        }
        var radialLength = Math.Sqrt(
            Math.Pow(contour[1].X - contour[0].X, 2) +
            Math.Pow(contour[1].Y - contour[0].Y, 2));
        var transverseLength = Math.Abs(contour[3].Z - contour[0].Z);
        if (radialLength <= StiffenerTolerance ||
            transverseLength <= StiffenerTolerance)
        {
            throw new InvalidOperationException(
                "Внутреннее ребро на отметке " +
                elevation.ToString("0.###", CultureInfo.InvariantCulture) +
                " получило вырожденный контур.");
        }
        if (chamfer * 2 >= Math.Min(radialLength, transverseLength))
        {
            throw new InvalidOperationException(
                "Фаска внутреннего ребра " +
                chamfer.ToString("0.###", CultureInfo.InvariantCulture) +
                " мм слишком велика для внутреннего контура коробки.");
        }
    }

    private static bool IsEnabledValue(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.Equals(normalized, "YES", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "TRUE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "ДА", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "1", StringComparison.Ordinal);
    }

    private static string NormalizeRequiredText(
        string value,
        string fallback,
        string label)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ||
            string.Equals(
                value.Trim(),
                int.MinValue.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            ? fallback
            : value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException(label + " не задан.");
        }
        return normalized;
    }

    private static double NormalizeStiffenerChamfer(double value)
    {
        if (double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value <= -1_000_000)
        {
            return DefaultStiffenerChamfer;
        }
        if (value < 0)
        {
            throw new InvalidOperationException(
                "Величина фаски внутреннего ребра не может быть отрицательной.");
        }
        return value;
    }

    private static FachwerkColumnStiffenerPlacementMode
        NormalizeStiffenerPlacementMode(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(
                normalized,
                int.MinValue.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            string.Equals(
                normalized,
                "ELEVATIONS",
                StringComparison.OrdinalIgnoreCase))
        {
            return FachwerkColumnStiffenerPlacementMode.Elevations;
        }
        if (string.Equals(
            normalized,
            "SPACING",
            StringComparison.OrdinalIgnoreCase))
        {
            return FachwerkColumnStiffenerPlacementMode.Spacing;
        }
        if (string.Equals(
            normalized,
            "AUTO",
            StringComparison.OrdinalIgnoreCase))
        {
            return FachwerkColumnStiffenerPlacementMode.Automatic;
        }
        throw new InvalidOperationException(
            "Неизвестный режим расстановки внутренних рёбер '" +
            normalized + "'.");
    }

    private static double NormalizePositiveStiffenerSpacing(double value)
    {
        if (double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value <= -1_000_000)
        {
            return 1500.0;
        }
        if (value <= StiffenerTolerance)
        {
            throw new InvalidOperationException(
                "Шаг авторасстановки внутренних рёбер должен быть положительным.");
        }
        return value;
    }

    private static double NormalizeNonNegativeStiffenerValue(
        double value,
        double fallback,
        string label)
    {
        if (double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value <= -1_000_000)
        {
            return fallback;
        }
        if (value < 0)
        {
            throw new InvalidOperationException(
                label + " не может быть отрицательным.");
        }
        return value;
    }

    private static bool TryParseStiffenerDouble(
        string value,
        out double parsed)
    {
        if (double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out parsed))
        {
            return true;
        }
        if (double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.CurrentCulture,
            out parsed))
        {
            return true;
        }
        return double.TryParse(
            (value ?? string.Empty).Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out parsed);
    }
}
