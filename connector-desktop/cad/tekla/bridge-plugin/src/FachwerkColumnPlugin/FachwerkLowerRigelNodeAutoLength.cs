#nullable disable

using System;
using System.Collections.Generic;
using Tekla.Structures.Geometry3d;

namespace Structura.Tekla.Fachwerk;

internal static class FachwerkLowerRigelNodeAutoLength
{
    internal const double TargetShortEdgeLength = 250.0;

    private const double MinimumTransitionLength = 10.0;
    private const double TargetTolerance = 0.05;
    private const int MinimumScanIntervals = 160;
    private const int MaximumScanIntervals = 4000;
    private const double MaximumScanStep = 25.0;
    private const int BisectionIterations = 60;

    internal static double Calculate(
        IReadOnlyList<Point> leftTubeAxis,
        IReadOnlyList<Point> rightTubeAxis,
        IReadOnlyList<Point> leftWebAxis,
        IReadOnlyList<Point> rightWebAxis,
        IReadOnlyList<Point> outerFlangeAxis,
        IReadOnlyList<Point> innerFlangeAxis,
        double gap,
        double diameter,
        double transitionPlateWidth,
        double currentLength)
    {
        double maximumLength = Math.Min(
            PolylineLength(leftWebAxis),
            PolylineLength(rightWebAxis)) - 0.1;
        if (maximumLength <= MinimumTransitionLength)
        {
            throw new InvalidOperationException(
                "Оси стенок слишком короткие для автоподбора длины пластин.");
        }

        var samples = new List<Sample>();
        Sample best = null;
        int scanIntervals = Math.Min(
            MaximumScanIntervals,
            Math.Max(
                MinimumScanIntervals,
                (int)Math.Ceiling(
                    (maximumLength - MinimumTransitionLength) /
                    MaximumScanStep)));
        for (var index = 0; index <= scanIntervals; index++)
        {
            double candidate = MinimumTransitionLength +
                (maximumLength - MinimumTransitionLength) *
                index / scanIntervals;
            if (!TryMeasure(
                    leftTubeAxis,
                    rightTubeAxis,
                    leftWebAxis,
                    rightWebAxis,
                    outerFlangeAxis,
                    innerFlangeAxis,
                    gap,
                    diameter,
                    transitionPlateWidth,
                    candidate,
                    out double shortEdge))
            {
                samples.Add(null);
                continue;
            }

            var sample = new Sample(candidate, shortEdge);
            samples.Add(sample);
            if (best == null ||
                Math.Abs(sample.Error) < Math.Abs(best.Error) ||
                Math.Abs(Math.Abs(sample.Error) - Math.Abs(best.Error)) < 1e-9 &&
                Math.Abs(sample.Length - currentLength) <
                Math.Abs(best.Length - currentLength))
            {
                best = sample;
            }
        }

        if (best == null)
        {
            throw new InvalidOperationException(
                "Не найден допустимый прямой участок для автоподбора длины пластин.");
        }
        if (Math.Abs(best.Error) <= TargetTolerance)
        {
            return best.Length;
        }

        Sample bracketStart = null;
        Sample bracketEnd = null;
        double bestBracketDistance = double.MaxValue;
        for (var index = 1; index < samples.Count; index++)
        {
            Sample first = samples[index - 1];
            Sample second = samples[index];
            if (first == null || second == null ||
                Math.Sign(first.Error) == Math.Sign(second.Error))
            {
                continue;
            }

            double midpoint = (first.Length + second.Length) * 0.5;
            double distance = Math.Abs(midpoint - currentLength);
            if (distance >= bestBracketDistance)
            {
                continue;
            }
            bracketStart = first;
            bracketEnd = second;
            bestBracketDistance = distance;
        }

        if (bracketStart == null || bracketEnd == null)
        {
            throw new InvalidOperationException(
                "Автоподбор не может получить боковую кромку 250 мм. " +
                "Ближайшее значение: " +
                best.ShortEdge.ToString("0.###") + " мм при длине " +
                best.Length.ToString("0.###") + " мм.");
        }

        for (var iteration = 0;
             iteration < BisectionIterations;
             iteration++)
        {
            double midpoint =
                (bracketStart.Length + bracketEnd.Length) * 0.5;
            double shortEdge = Measure(
                leftTubeAxis,
                rightTubeAxis,
                leftWebAxis,
                rightWebAxis,
                outerFlangeAxis,
                innerFlangeAxis,
                gap,
                diameter,
                transitionPlateWidth,
                midpoint);
            var sample = new Sample(midpoint, shortEdge);
            if (Math.Abs(sample.Error) <= TargetTolerance)
            {
                return sample.Length;
            }
            if (Math.Sign(sample.Error) == Math.Sign(bracketStart.Error))
            {
                bracketStart = sample;
            }
            else
            {
                bracketEnd = sample;
            }
        }

        return Math.Abs(bracketStart.Error) <= Math.Abs(bracketEnd.Error)
            ? bracketStart.Length
            : bracketEnd.Length;
    }

    internal static double Measure(
        IReadOnlyList<Point> leftTubeAxis,
        IReadOnlyList<Point> rightTubeAxis,
        IReadOnlyList<Point> leftWebAxis,
        IReadOnlyList<Point> rightWebAxis,
        IReadOnlyList<Point> outerFlangeAxis,
        IReadOnlyList<Point> innerFlangeAxis,
        double gap,
        double diameter,
        double transitionPlateWidth,
        double transitionLength)
    {
        FachwerkLowerRigelLayout layout =
            FachwerkLowerRigelNodeGeometry.BuildLayout(
                leftTubeAxis,
                rightTubeAxis,
                leftWebAxis,
                rightWebAxis,
                gap,
                diameter,
                transitionPlateWidth,
                transitionLength,
                transitionLength);
        FachwerkLowerRigelMontageLayout montage =
            FachwerkLowerRigelNodeGeometry.BuildMontageLayout(layout);
        Vector centralTubeDirection =
            FachwerkLowerRigelNodeGeometry.VectorBetween(
                layout.LeftCentralTubePoint,
                layout.RightCentralTubePoint);
        FachwerkLowerRigelFlangeSplice innerSplice =
            FachwerkLowerRigelNodeGeometry.BuildFlangeSplice(
                "inner-flange",
                innerFlangeAxis,
                outerFlangeAxis,
                montage,
                layout.LeftCentralTubePoint,
                centralTubeDirection,
                diameter * 0.5);
        IReadOnlyList<Point> boundary = innerSplice.LowerFragmentBoundary;
        if (boundary == null || boundary.Count != 4)
        {
            throw new InvalidOperationException(
                "Нижний фрагмент внутреннего пояса не получил четырёхточечный контур.");
        }

        double tubeRadius = diameter * 0.5;
        if (DistanceToAxis(
                boundary[0],
                layout.LeftCentralTubePoint,
                centralTubeDirection) < tubeRadius - 1e-3 ||
            DistanceToAxis(
                boundary[3],
                layout.LeftCentralTubePoint,
                centralTubeDirection) < tubeRadius - 1e-3)
        {
            throw new InvalidOperationException(
                "Монтажная кромка нижнего внутреннего пояса находится внутри трубы.");
        }

        double firstEdge = FachwerkLowerRigelNodeGeometry.Distance(
            boundary[0],
            boundary[1]);
        double secondEdge = FachwerkLowerRigelNodeGeometry.Distance(
            boundary[3],
            boundary[2]);
        return Math.Min(firstEdge, secondEdge);
    }

    private static double DistanceToAxis(
        Point point,
        Point axisPoint,
        Vector axisDirection)
    {
        Vector axis = FachwerkLowerRigelNodeGeometry.Scale(
            axisDirection,
            1.0 / Math.Sqrt(
                FachwerkLowerRigelNodeGeometry.Dot(
                    axisDirection,
                    axisDirection)));
        Vector relative = FachwerkLowerRigelNodeGeometry.VectorBetween(
            axisPoint,
            point);
        Vector perpendicular = FachwerkLowerRigelNodeGeometry.Subtract(
            relative,
            FachwerkLowerRigelNodeGeometry.Scale(
                axis,
                FachwerkLowerRigelNodeGeometry.Dot(relative, axis)));
        return Math.Sqrt(
            FachwerkLowerRigelNodeGeometry.Dot(
                perpendicular,
                perpendicular));
    }

    private static bool TryMeasure(
        IReadOnlyList<Point> leftTubeAxis,
        IReadOnlyList<Point> rightTubeAxis,
        IReadOnlyList<Point> leftWebAxis,
        IReadOnlyList<Point> rightWebAxis,
        IReadOnlyList<Point> outerFlangeAxis,
        IReadOnlyList<Point> innerFlangeAxis,
        double gap,
        double diameter,
        double transitionPlateWidth,
        double transitionLength,
        out double shortEdge)
    {
        try
        {
            shortEdge = Measure(
                leftTubeAxis,
                rightTubeAxis,
                leftWebAxis,
                rightWebAxis,
                outerFlangeAxis,
                innerFlangeAxis,
                gap,
                diameter,
                transitionPlateWidth,
                transitionLength);
            return !double.IsNaN(shortEdge) &&
                !double.IsInfinity(shortEdge);
        }
        catch (InvalidOperationException)
        {
            shortEdge = 0;
            return false;
        }
    }

    private static double PolylineLength(IReadOnlyList<Point> points)
    {
        if (points == null || points.Count < 2)
        {
            return 0;
        }
        double result = 0;
        for (var index = 0; index < points.Count - 1; index++)
        {
            result += FachwerkLowerRigelNodeGeometry.Distance(
                points[index],
                points[index + 1]);
        }
        return result;
    }

    private sealed class Sample
    {
        internal Sample(double length, double shortEdge)
        {
            Length = length;
            ShortEdge = shortEdge;
        }

        internal double Length { get; }
        internal double ShortEdge { get; }
        internal double Error => ShortEdge - TargetShortEdgeLength;
    }
}
