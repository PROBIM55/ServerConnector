#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace Structura.Tekla.Fachwerk;

internal sealed class FachwerkColumnAssemblySegmentPlan
{
    internal FachwerkColumnAssemblySegmentPlan(
        string segmentId,
        int assemblySegmentIndex,
        double lowerElevationMm,
        double upperElevationMm,
        int mainPartIndex,
        IReadOnlyList<int> memberPartIndices)
    {
        SegmentId = segmentId;
        AssemblySegmentIndex = assemblySegmentIndex;
        LowerElevationMm = lowerElevationMm;
        UpperElevationMm = upperElevationMm;
        MainPartIndex = mainPartIndex;
        MemberPartIndices = memberPartIndices;
        WeldSecondaryPartIndices = new ReadOnlyCollection<int>(
            memberPartIndices.Where(index => index != mainPartIndex).ToList());
    }

    public string SegmentId { get; }
    public int AssemblySegmentIndex { get; }
    public double LowerElevationMm { get; }
    public double UpperElevationMm { get; }
    public int MainPartIndex { get; }
    public IReadOnlyList<int> MemberPartIndices { get; }
    public IReadOnlyList<int> WeldSecondaryPartIndices { get; }
}

internal sealed class FachwerkColumnAssemblyPlan
{
    internal FachwerkColumnAssemblyPlan(
        IReadOnlyList<FachwerkColumnAssemblySegmentPlan> segments,
        IReadOnlyList<int> assignedPartIndices)
    {
        Segments = segments;
        AssignedPartIndices = assignedPartIndices;
    }

    public IReadOnlyList<FachwerkColumnAssemblySegmentPlan> Segments { get; }
    public IReadOnlyList<int> AssignedPartIndices { get; }
}

internal static class FachwerkColumnAssemblyPlanner
{
    internal const double DefaultToleranceMm = 0.25;

    public static FachwerkColumnAssemblyPlan Build(
        string existingKmMark,
        IReadOnlyList<FachwerkColumnPartSpec> sourceParts,
        double toleranceMm = DefaultToleranceMm)
    {
        string mark = FachwerkPartRoleContract.RequireText(existingKmMark, nameof(existingKmMark));
        FachwerkAttributeMapper.BuildColumnAssemblyPrefix(mark);
        if (sourceParts == null) throw new ArgumentNullException(nameof(sourceParts));
        if (sourceParts.Count == 0)
            throw new ArgumentException("Для сборок стойки не передано ни одной физической детали.", nameof(sourceParts));
        if (!IsFinite(toleranceMm) || toleranceMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm), toleranceMm, "Допуск должен быть положительным.");

        var indexed = sourceParts
            .Select((part, index) => IndexedPart.Create(index, part))
            .ToList();
        var outerGroups = indexed
            .Where(item => item.Role == FachwerkPartRole.OuterFlange)
            .GroupBy(item => item.AssemblySegmentIndex)
            .OrderBy(group => group.Key)
            .ToList();

        if (outerGroups.Count == 0)
            throw new InvalidOperationException(
                "Геометрия стойки не содержит outer-flange, определяющего реальные границы сборок.");

        var groups = new List<MutableSegment>(outerGroups.Count);
        foreach (var outerGroup in outerGroups)
        {
            var outerFlanges = outerGroup.ToArray();
            if (outerFlanges.Length != 1)
            {
                throw new InvalidOperationException(
                    "Сборочный сегмент " + outerGroup.Key.ToString(CultureInfo.InvariantCulture) +
                    " должен содержать ровно одну физическую деталь outer-flange, получено " +
                    outerFlanges.Length.ToString(CultureInfo.InvariantCulture) + ".");
            }
            groups.Add(new MutableSegment(mark, outerGroup.Key, outerFlanges[0]));
        }

        foreach (var part in indexed.Where(item => item.Role != FachwerkPartRole.OuterFlange))
        {
            MutableSegment target = groups.SingleOrDefault(
                group => group.AssemblySegmentIndex == part.AssemblySegmentIndex);
            if (target == null)
            {
                throw new InvalidOperationException(
                    "Для детали не найден явно назначенный сборочный сегмент: " +
                    part.Describe() + ".");
            }
            target.Add(part);
        }

        var assigned = new HashSet<int>();
        var result = new List<FachwerkColumnAssemblySegmentPlan>();
        foreach (var group in groups)
        {
            group.ValidateRequiredRoles();
            var members = group.Parts
                .OrderBy(item => RoleOrder(item.Role))
                .ThenBy(item => item.SourceIndex)
                .Select(item => item.SourceIndex)
                .ToList();
            foreach (int sourceIndex in members)
            {
                if (!assigned.Add(sourceIndex))
                    throw new InvalidOperationException("Физическая деталь назначена более чем одной сборке: index=" + sourceIndex + ".");
            }

            result.Add(new FachwerkColumnAssemblySegmentPlan(
                group.SegmentId,
                group.AssemblySegmentIndex,
                group.LowerElevationMm,
                group.UpperElevationMm,
                group.MainPart.SourceIndex,
                new ReadOnlyCollection<int>(members)));
        }

        if (assigned.Count != sourceParts.Count)
        {
            var missing = Enumerable.Range(0, sourceParts.Count).Where(index => !assigned.Contains(index));
            throw new InvalidOperationException(
                "Не все физические детали назначены сборкам стойки: " + string.Join(", ", missing) + ".");
        }

        return new FachwerkColumnAssemblyPlan(
            new ReadOnlyCollection<FachwerkColumnAssemblySegmentPlan>(result),
            new ReadOnlyCollection<int>(assigned.OrderBy(index => index).ToList()));
    }

    private static int RoleOrder(FachwerkPartRole role)
    {
        switch (role)
        {
            case FachwerkPartRole.LeftWeb: return 0;
            case FachwerkPartRole.RightWeb: return 1;
            case FachwerkPartRole.InnerFlange: return 2;
            case FachwerkPartRole.OuterFlange: return 3;
            default: return 99;
        }
    }

    private sealed class MutableSegment
    {
        private readonly List<IndexedPart> _parts = new List<IndexedPart>();

        public MutableSegment(
            string mark,
            int assemblySegmentIndex,
            IndexedPart outerFlange)
        {
            if (outerFlange.Role != FachwerkPartRole.OuterFlange)
                throw new ArgumentException("Границу сборки должен задавать outer-flange.", nameof(outerFlange));

            AssemblySegmentIndex = assemblySegmentIndex;
            LowerElevationMm = outerFlange.LowerElevationMm;
            UpperElevationMm = outerFlange.UpperElevationMm;
            SegmentId = "fachwerk/assembly/" + Uri.EscapeDataString(mark) + "/" +
                "segment-" + assemblySegmentIndex.ToString(CultureInfo.InvariantCulture) + "/" +
                FormatBound(LowerElevationMm) + "/" + FormatBound(UpperElevationMm) +
                "/end-" + outerFlange.EndBreakIndex.ToString(CultureInfo.InvariantCulture);
            Add(outerFlange);
        }

        public string SegmentId { get; }
        public int AssemblySegmentIndex { get; }
        public double LowerElevationMm { get; }
        public double UpperElevationMm { get; }
        public IndexedPart MainPart => _parts
            .Where(item => item.Role == FachwerkPartRole.LeftWeb)
            .OrderBy(item => item.LowerElevationMm)
            .ThenBy(item => item.SourceIndex)
            .First();
        public IEnumerable<IndexedPart> Parts => _parts;

        public void Add(IndexedPart part)
        {
            if (_parts.Any(item => item.SourceIndex == part.SourceIndex))
            {
                throw new InvalidOperationException(
                    "В сегменте '" + SegmentId + "' повторно назначена физическая деталь index=" +
                    part.SourceIndex.ToString(CultureInfo.InvariantCulture) + ".");
            }
            _parts.Add(part);
        }

        public void ValidateRequiredRoles()
        {
            foreach (var role in new[]
            {
                FachwerkPartRole.LeftWeb,
                FachwerkPartRole.RightWeb,
                FachwerkPartRole.InnerFlange,
            })
            {
                if (!_parts.Any(item => item.Role == role))
                {
                    throw new InvalidOperationException(
                        "В сегменте '" + SegmentId + "' отсутствует обязательная роль " +
                        FachwerkPartRoleContract.ToSemanticName(role) + ".");
                }
            }
        }

        private static string FormatBound(double value)
        {
            return Math.Round(value, 3, MidpointRounding.AwayFromZero)
                .ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    private sealed class IndexedPart
    {
        private IndexedPart(
            int sourceIndex,
            FachwerkPartRole role,
            int endBreakIndex,
            int assemblySegmentIndex,
            double lowerElevationMm,
            double upperElevationMm)
        {
            SourceIndex = sourceIndex;
            Role = role;
            EndBreakIndex = endBreakIndex;
            AssemblySegmentIndex = assemblySegmentIndex;
            LowerElevationMm = lowerElevationMm;
            UpperElevationMm = upperElevationMm;
        }

        public int SourceIndex { get; }
        public FachwerkPartRole Role { get; }
        public int EndBreakIndex { get; }
        public int AssemblySegmentIndex { get; }
        public double LowerElevationMm { get; }
        public double UpperElevationMm { get; }

        public static IndexedPart Create(int sourceIndex, FachwerkColumnPartSpec part)
        {
            if (part == null) throw new ArgumentNullException(nameof(part));
            if (part.FirstBoundary == null || part.FirstBoundary.Count == 0)
                throw new InvalidOperationException("У физической детали отсутствует траектория: index=" + sourceIndex + ".");

            double lower = double.PositiveInfinity;
            double upper = double.NegativeInfinity;
            foreach (var primitive in part.FirstBoundary)
            {
                lower = Math.Min(lower, Math.Min(primitive.Start.Y, primitive.End.Y));
                upper = Math.Max(upper, Math.Max(primitive.Start.Y, primitive.End.Y));
            }
            if (!IsFinite(lower) || !IsFinite(upper) || upper - lower <= DefaultToleranceMm)
                throw new InvalidOperationException("Некорректный высотный интервал физической детали: index=" + sourceIndex + ".");

            return new IndexedPart(
                sourceIndex,
                FachwerkPartRoleContract.FromColumnGeometryRole(part.Role),
                part.EndBreakIndex,
                part.AssemblySegmentIndex,
                lower,
                upper);
        }

        public string Describe()
        {
            return "index=" + SourceIndex + ", role=" + FachwerkPartRoleContract.ToSemanticName(Role) +
                ", endBreak=" + EndBreakIndex.ToString(CultureInfo.InvariantCulture) +
                ", assemblySegment=" + AssemblySegmentIndex.ToString(CultureInfo.InvariantCulture) +
                ", interval=[" + LowerElevationMm.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                UpperElevationMm.ToString("0.###", CultureInfo.InvariantCulture) + "]";
        }
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
