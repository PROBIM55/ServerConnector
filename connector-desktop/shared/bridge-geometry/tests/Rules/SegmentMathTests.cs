using System;
using System.Collections.Generic;
using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Rules;
using FluentAssertions;
using Xunit;

namespace Platform.Bridge.Geometry.Tests.Rules;

public class SegmentMathTests
{
    [Fact]
    public void SumLength_Cases()
    {
        SegmentMath.SumLength(new SegLwt[0]).Should().Be(0);
        SegmentMath.SumLength(new[] { new SegLwt(1000, 400, 30) }).Should().Be(1000);
        SegmentMath.SumLength(new[] { new SegLwt(1000, 400, 30), new SegLwt(500, 400, 30) }).Should().Be(1500);
    }

    [Fact]
    public void MaxThickness_Cases()
    {
        SegmentMath.MaxThickness(new SegLwt[0]).Should().Be(0);
        SegmentMath.MaxThickness(new[] { new SegLwt(1000, 400, 30), new SegLwt(500, 400, 50) }).Should().Be(50);
        SegmentMath.MaxThickness(new[] { new SegLwt(1000, 400, 50), new SegLwt(500, 400, 30) }).Should().Be(50);
    }

    [Fact]
    public void MaxWidth_Cases()
    {
        SegmentMath.MaxWidth(new SegLwt[0]).Should().Be(0);
        SegmentMath.MaxWidth(new[] { new SegLwt(1000, 400, 30), new SegLwt(500, 500, 30) }).Should().Be(500);
    }

    [Fact]
    public void AllPositive_Cases()
    {
        SegmentMath.AllPositive(new SegLwt[0]).Should().BeTrue();
        SegmentMath.AllPositive(new[] { new SegLwt(1000, 400, 30) }).Should().BeTrue();
        SegmentMath.AllPositive(new[] { new SegLwt(0, 400, 30) }).Should().BeFalse();
        SegmentMath.AllPositive(new[] { new SegLwt(1000, 0, 30) }).Should().BeFalse();
        SegmentMath.AllPositive(new[] { new SegLwt(1000, 400, 0) }).Should().BeFalse();
        SegmentMath.AllPositive(new[] { new SegLwt(-1, 400, 30) }).Should().BeFalse();
    }

    [Fact]
    public void HasSectionVariation_FalseForEmptyOrSingle()
    {
        SegmentMath.HasSectionVariation(null).Should().BeFalse();
        SegmentMath.HasSectionVariation(new SegLwt[0]).Should().BeFalse();
        SegmentMath.HasSectionVariation(new[] { new SegLwt(1000, 400, 30) }).Should().BeFalse();
    }

    [Fact]
    public void HasSectionVariation_FalseForAllSame()
    {
        var segs = new[]
        {
            new SegLwt(1000, 400, 30),
            new SegLwt(2000, 400, 30),
            new SegLwt(1500, 400, 30),
        };
        SegmentMath.HasSectionVariation(segs).Should().BeFalse();
    }

    [Fact]
    public void HasSectionVariation_TrueOnWidthChange()
    {
        var segs = new[]
        {
            new SegLwt(1000, 400, 30),
            new SegLwt(1000, 500, 30),
        };
        SegmentMath.HasSectionVariation(segs).Should().BeTrue();
    }

    [Fact]
    public void HasSectionVariation_TrueOnThicknessChange()
    {
        var segs = new[]
        {
            new SegLwt(1000, 400, 30),
            new SegLwt(1000, 400, 50),
        };
        SegmentMath.HasSectionVariation(segs).Should().BeTrue();
    }

    [Fact]
    public void HasSectionVariation_FalseWhenChangeBelowEpsilon()
    {
        // 0.005 < SectionEpsilon (0.01) — should be treated as no change.
        var segs = new[]
        {
            new SegLwt(1000, 400, 30),
            new SegLwt(1000, 400.005, 30),
        };
        SegmentMath.HasSectionVariation(segs).Should().BeFalse();
    }

    [Fact]
    public void BuildBreakpoints_MergesAndSorts()
    {
        var flange = new[] { new SegLwt(1000, 400, 30), new SegLwt(2000, 400, 30) };
        var web = new[] { new SegLwt(1500, 2000, 16), new SegLwt(1500, 2000, 16) };
        var bps = SegmentMath.BuildBreakpoints(3000, flange, web);
        // expected: {0, 1000 (flange#0 end), 1500 (web#0 end), 3000}
        // (flange#1 end = 3000 not added since !< axisLen; web#1 end = 3000 same)
        bps.Should().Equal(0.0, 1000.0, 1500.0, 3000.0);
    }

    [Fact]
    public void BuildBreakpoints_DedupsCommonInternalBoundaries()
    {
        var flange = new[] { new SegLwt(1500, 400, 30), new SegLwt(1500, 400, 30) };
        var web = new[] { new SegLwt(1500, 2000, 16), new SegLwt(1500, 2000, 16) };
        var bps = SegmentMath.BuildBreakpoints(3000, flange, web);
        bps.Should().Equal(0.0, 1500.0, 3000.0);
    }

    [Fact]
    public void ParamsAt_BoundaryCases()
    {
        var segs = new[]
        {
            new SegLwt(1000, 400, 30),
            new SegLwt(2000, 500, 40),
        };
        // s=0 → seg[0]
        SegmentMath.ParamsAt(segs, 0).Should().Be(segs[0]);
        // s exactly at boundary 1000 → still seg[0] (s <= cursor)
        SegmentMath.ParamsAt(segs, 1000).Should().Be(segs[0]);
        // s past boundary → seg[1]
        SegmentMath.ParamsAt(segs, 1500).Should().Be(segs[1]);
        // s past end → last seg (i == segs.Length-1 fallback)
        SegmentMath.ParamsAt(segs, 5000).Should().Be(segs[1]);
    }

    [Fact]
    public void CollectBreaks_OnlyInRange()
    {
        var cuts = new[]
        {
            new ThicknessCutSpec(0, true, 1000, 900, 1100, 50, 30, 0, 0),
            new ThicknessCutSpec(1, false, 2000, 1900, 2100, 30, 50, 0, 2),
        };
        var breaks = SegmentMath.CollectBreaks(800, 1200, cuts);
        breaks.Should().Equal(800.0, 900.0, 1100.0, 1200.0);
    }

    [Fact]
    public void CollectBreaks_BoundaryExcluded()
    {
        // cut endpoint EXACTLY at s0 / s1 must NOT be added (it's not strictly inside).
        var cuts = new[]
        {
            new ThicknessCutSpec(0, true, 0, 800, 1200, 50, 30, 0, 0),
        };
        var breaks = SegmentMath.CollectBreaks(800, 1200, cuts);
        breaks.Should().Equal(800.0, 1200.0);
    }

    [Fact]
    public void ThicknessAt_OutsideCuts_UsesParamsAt()
    {
        var segs = new[]
        {
            new SegLwt(1000, 400, 30),
            new SegLwt(2000, 400, 50),
        };
        var cuts = new[]
        {
            new ThicknessCutSpec(0, false, 1000, 900, 1100, 50, 30, 0, 1),
        };
        SegmentMath.ThicknessAt(segs, cuts, 100).Should().Be(30); // before cut → seg[0] T
        SegmentMath.ThicknessAt(segs, cuts, 2000).Should().Be(50); // after cut → seg[1] T
    }

    [Fact]
    public void ThicknessAt_InsideCut_LinearInterp()
    {
        var segs = new[]
        {
            new SegLwt(1000, 400, 30),
            new SegLwt(2000, 400, 50),
        };
        // Decrease=false → thickness increases from 30→50 across 900..1100.
        var cuts = new[]
        {
            new ThicknessCutSpec(0, false, 1000, 900, 1100, 50, 30, 0, 1),
        };
        SegmentMath.ThicknessAt(segs, cuts, 900).Should().BeApproximately(30, 1e-9);
        SegmentMath.ThicknessAt(segs, cuts, 1100).Should().BeApproximately(50, 1e-9);
        SegmentMath.ThicknessAt(segs, cuts, 1000).Should().BeApproximately(40, 1e-9);
    }
}
