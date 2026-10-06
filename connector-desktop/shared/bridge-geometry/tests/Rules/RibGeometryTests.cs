using System.Linq;
using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Rules;
using FluentAssertions;
using Xunit;

namespace Platform.Bridge.Geometry.Tests.Rules;

/// <summary>
/// Тесты SplitRunByWebThickness: продольное ребро НЕ наследует продольную
/// разбивку стенки, а бьётся отдельной плитой ТОЛЬКО на изменении толщины
/// стенки (чтобы сохранить высоту от поверхности на уступе). Паритет с
/// web-side buildWebThicknessSpans (beam-placement.test.ts).
/// </summary>
public class RibGeometryTests
{
    private static SegLwt[] Web(params double[] thicknesses) =>
        thicknesses.Select(t => new SegLwt(3000, 0, t)).ToArray();

    private static RibGeometry.RunRange Run(double start, double end) =>
        new(start, end);

    [Fact]
    public void SplitRunByWebThickness_ConstantThickness_OnePiece()
    {
        // Одна толщина на 3 сегментах → один цельный участок (нет наследования).
        var pieces = RibGeometry.SplitRunByWebThickness(Run(0, 9000), Web(16, 16, 16));
        pieces.Should().HaveCount(1);
        pieces[0].Start.Should().Be(0);
        pieces[0].End.Should().Be(9000);
    }

    [Fact]
    public void SplitRunByWebThickness_ThicknessChange_BreaksAtBoundary()
    {
        var pieces = RibGeometry.SplitRunByWebThickness(Run(0, 9000), Web(16, 20, 20));
        pieces.Should().HaveCount(2);
        pieces[0].Start.Should().Be(0);
        pieces[0].End.Should().Be(3000);
        pieces[1].Start.Should().Be(3000);
        pieces[1].End.Should().Be(9000);
    }

    [Fact]
    public void SplitRunByWebThickness_ThickMiddle_ThreePieces()
    {
        // 16→20→16: ребро шагает наружу и обратно.
        var pieces = RibGeometry.SplitRunByWebThickness(Run(0, 9000), Web(16, 20, 16));
        pieces.Should().HaveCount(3);
        pieces.Select(p => p.Start).Should().Equal(0, 3000, 6000);
        pieces.Select(p => p.End).Should().Equal(3000, 6000, 9000);
    }

    [Fact]
    public void SplitRunByWebThickness_PartialRun_OnlyBoundariesInsideRun()
    {
        // Ребро только 2000..7000; границы толщины на 3000 и 6000 → 3 куска,
        // обрезанных по диапазону ребра.
        var pieces = RibGeometry.SplitRunByWebThickness(Run(2000, 7000), Web(16, 20, 16));
        pieces.Should().HaveCount(3);
        pieces[0].Start.Should().Be(2000);
        pieces[0].End.Should().Be(3000);
        pieces[1].Start.Should().Be(3000);
        pieces[1].End.Should().Be(6000);
        pieces[2].Start.Should().Be(6000);
        pieces[2].End.Should().Be(7000);
    }

    [Fact]
    public void SplitRunByWebThickness_EpsilonDifference_Coalesced()
    {
        // Дрейф толщины внутри epsilon (сравнение соседних) → один участок.
        var web = new[] { new SegLwt(3000, 0, 16), new SegLwt(3000, 0, 16.008), new SegLwt(3000, 0, 16.016) };
        var pieces = RibGeometry.SplitRunByWebThickness(Run(0, 9000), web);
        pieces.Should().HaveCount(1);
        pieces[0].End.Should().Be(9000);
    }

    [Fact]
    public void SplitRunByWebThickness_EmptyWeb_WholeRun()
    {
        var pieces = RibGeometry.SplitRunByWebThickness(Run(0, 9000), new SegLwt[0]);
        pieces.Should().HaveCount(1);
        pieces[0].Start.Should().Be(0);
        pieces[0].End.Should().Be(9000);
    }

    [Fact]
    public void SplitRunByWebThickness_DegenerateRun_Empty()
    {
        RibGeometry.SplitRunByWebThickness(Run(5000, 5000), Web(16, 20)).Should().BeEmpty();
    }

    // ----------------- Фаза 2: участки + скос (паритет с web-side) -----------------

    [Fact]
    public void ResolveRibSections_NoSegments_WholeRun()
    {
        var secs = RibGeometry.ResolveRibSections(null, Run(0, 9000), 160, 14);
        secs.Should().HaveCount(1);
        secs[0].Start.Should().Be(0);
        secs[0].End.Should().Be(9000);
        secs[0].Height.Should().Be(160);
        secs[0].Thickness.Should().Be(14);
    }

    [Fact]
    public void ResolveRibSections_FitsToRun_LastRemainder_AbsoluteX()
    {
        var segs = new[]
        {
            new RibSegment(3000, 200, 16),
            new RibSegment(3000, 160, 14),
            new RibSegment(1000, 120, 12),
        };
        // Run.Span 9000, заданная сумма 7000 → последний растягивается на остаток.
        var secs = RibGeometry.ResolveRibSections(segs, Run(500, 9500), 160, 14);
        secs.Should().HaveCount(3);
        secs[0].Start.Should().Be(500); secs[0].End.Should().Be(3500); secs[0].Height.Should().Be(200);
        secs[1].Start.Should().Be(3500); secs[1].End.Should().Be(6500); secs[1].Height.Should().Be(160);
        secs[2].Start.Should().Be(6500); secs[2].End.Should().Be(9500); secs[2].Height.Should().Be(120);
    }

    [Fact]
    public void ResolveRibSections_NonPositiveHT_FallbackToNominal()
    {
        var segs = new[] { new RibSegment(4500, 0, 0), new RibSegment(4500, 0, 0) };
        var secs = RibGeometry.ResolveRibSections(segs, Run(0, 9000), 160, 14);
        secs.Should().OnlyContain(s => s.Height == 160 && s.Thickness == 14);
    }

    [Fact]
    public void BuildRibProfile_SameHeight_OneFlat()
    {
        var sections = new[] { new RibGeometry.RibSection(0, 9000, 160, 14) };
        var p = RibGeometry.BuildRibProfile(sections, 4);
        p.Should().HaveCount(1);
        p[0].IsRamp.Should().BeFalse();
        p[0].Start.Should().Be(0);
        p[0].End.Should().Be(9000);
    }

    [Fact]
    public void BuildRibProfile_HeightTransition_FlatRampFlat()
    {
        var sections = new[]
        {
            new RibGeometry.RibSection(0, 4500, 160, 14),
            new RibGeometry.RibSection(4500, 9000, 200, 14),
        };
        var p = RibGeometry.BuildRibProfile(sections, 4);
        // L = |200-160|·4 = 160; flat [0,4340], скос [4340,4500] 160→200, flat [4500,9000].
        p.Should().HaveCount(3);
        p[0].End.Should().BeApproximately(4340, 1e-6); p[0].IsRamp.Should().BeFalse();
        p[1].Start.Should().BeApproximately(4340, 1e-6); p[1].End.Should().Be(4500);
        p[1].HeightStart.Should().Be(160); p[1].HeightEnd.Should().Be(200); p[1].IsRamp.Should().BeTrue();
        p[2].Start.Should().Be(4500); p[2].End.Should().Be(9000); p[2].IsRamp.Should().BeFalse();
    }

    [Fact]
    public void BuildRibProfile_ClampToSectionLength()
    {
        var sections = new[]
        {
            new RibGeometry.RibSection(0, 500, 100, 14),
            new RibGeometry.RibSection(500, 9000, 300, 14),
        };
        // Δh=200, L=800 клампится длиной левого (500) → весь левый = скос.
        var p = RibGeometry.BuildRibProfile(sections, 4);
        p.Should().HaveCount(2);
        p[0].Start.Should().Be(0); p[0].End.Should().Be(500); p[0].IsRamp.Should().BeTrue();
        p[0].HeightStart.Should().Be(100); p[0].HeightEnd.Should().Be(300);
        p[1].Start.Should().Be(500); p[1].End.Should().Be(9000); p[1].IsRamp.Should().BeFalse();
    }

    [Fact]
    public void RibHeightAt_LinearAndClamp()
    {
        var ramp = new RibGeometry.RibProfilePiece(1000, 1200, 160, 200, 14);
        RibGeometry.RibHeightAt(ramp, 1000).Should().BeApproximately(160, 1e-9);
        RibGeometry.RibHeightAt(ramp, 1100).Should().BeApproximately(180, 1e-9);
        RibGeometry.RibHeightAt(ramp, 1200).Should().BeApproximately(200, 1e-9);
        RibGeometry.RibHeightAt(ramp, 500).Should().BeApproximately(160, 1e-9);
        RibGeometry.RibHeightAt(ramp, 5000).Should().BeApproximately(200, 1e-9);
    }
}
