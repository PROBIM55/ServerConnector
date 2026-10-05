using System.Collections.Generic;
using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Rules;
using FluentAssertions;
using Xunit;

namespace Platform.Bridge.Geometry.Tests.Rules;

/// <summary>
/// Численные сценарии для портированного GroupDetails / BuildThicknessCuts.
/// Эталонные значения вычислены вручную из алгоритма decomp'а
/// (TeklaBridge.exe :216-356) и зафиксированы здесь. Любое отклонение от
/// этих чисел = регрессия binary-parity с прежним плагином (Phase F).
/// </summary>
public class FlangeTransitionRulesTests
{
    // Канонический stress-zone ratio для нижнего пояса в BOTTOM_TENSION схеме.
    private const double BottomRatio = 8.0;
    private const double TopRatio = 4.0;
    private const double Tol = 1e-6;

    // ---------------------------------------------------------------
    // T1: Single segment → 1 detail, no transitions, no cuts
    // ---------------------------------------------------------------

    [Fact]
    public void T1_SingleSegment_OneDetail_NoCuts()
    {
        var segs = new[] { new SegLwt(24000, 400, 30) };

        var details = FlangeTransitionRules.GroupDetails(segs, BottomRatio);
        var cuts = FlangeTransitionRules.BuildThicknessCuts(segs, BottomRatio);

        details.Should().HaveCount(1);
        AssertDetail(details[0], start: 0, end: 24000, w: 400, t: 30, ts: 0, te: 0);
        cuts.Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    // T2: Two same-section segments → merged into 1 detail
    // ---------------------------------------------------------------

    [Fact]
    public void T2_TwoSameSegments_MergedToOneDetail()
    {
        var segs = new[]
        {
            new SegLwt(12000, 400, 30),
            new SegLwt(12000, 400, 30),
        };

        var details = FlangeTransitionRules.GroupDetails(segs, BottomRatio);
        var cuts = FlangeTransitionRules.BuildThicknessCuts(segs, BottomRatio);

        details.Should().HaveCount(1);
        AssertDetail(details[0], start: 0, end: 24000, w: 400, t: 30, ts: 0, te: 0);
        cuts.Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    // T3: Width-only change → 2 details with width-taper on wider plate
    // ---------------------------------------------------------------

    [Fact]
    public void T3_WidthOnlyChange_TaperOnWiderPlate()
    {
        // ΔW = 100, ratio = 8 → taper length = 100/2 * 8 = 400.
        // Next plate (500) is wider → taper at its start.
        var segs = new[]
        {
            new SegLwt(8000, 400, 30),
            new SegLwt(16000, 500, 30),
        };

        var details = FlangeTransitionRules.GroupDetails(segs, BottomRatio);
        var cuts = FlangeTransitionRules.BuildThicknessCuts(segs, BottomRatio);

        details.Should().HaveCount(2);
        AssertDetail(details[0], start: 0, end: 7600, w: 400, t: 30, ts: 0, te: 0);
        AssertDetail(details[1], start: 7600, end: 24000, w: 500, t: 30, ts: 400, te: 0);
        cuts.Should().BeEmpty(); // width-only doesn't generate thickness cut
    }

    [Fact]
    public void T3b_WidthChangeReversed_CurrentIsWider()
    {
        // Current (500) wider → its end has the taper, extending past joint.
        var segs = new[]
        {
            new SegLwt(8000, 500, 30),
            new SegLwt(16000, 400, 30),
        };
        var details = FlangeTransitionRules.GroupDetails(segs, BottomRatio);

        details.Should().HaveCount(2);
        AssertDetail(details[0], start: 0, end: 8400, w: 500, t: 30, ts: 0, te: 400);
        AssertDetail(details[1], start: 8400, end: 24000, w: 400, t: 30, ts: 0, te: 0);
    }

    // ---------------------------------------------------------------
    // T4: Thickness-only change → 2 details + 1 ThicknessCutSpec
    // ---------------------------------------------------------------

    [Fact]
    public void T4_ThicknessOnlyChange_CutOnThickerPlate()
    {
        // ΔT = 20, ratio = 8 → taper length = 20 * 8 = 160.
        // Next plate (T=50) thicker → cut on it, Decrease=false.
        var segs = new[]
        {
            new SegLwt(8000, 400, 30),
            new SegLwt(16000, 400, 50),
        };

        var details = FlangeTransitionRules.GroupDetails(segs, BottomRatio);
        var cuts = FlangeTransitionRules.BuildThicknessCuts(segs, BottomRatio);

        details.Should().HaveCount(2);
        AssertDetail(details[0], start: 0, end: 7840, w: 400, t: 30, ts: 0, te: 0);
        AssertDetail(details[1], start: 7840, end: 24000, w: 400, t: 50, ts: 0, te: 0);

        cuts.Should().HaveCount(1);
        var c = cuts[0];
        c.BoundaryIndex.Should().Be(0);
        c.Decrease.Should().BeFalse();
        c.Theoretical.Should().BeApproximately(8000, Tol);
        c.Start.Should().BeApproximately(7840, Tol);
        c.End.Should().BeApproximately(8000, Tol);
        c.ThickT.Should().Be(50);
        c.ThinT.Should().Be(30);
        c.WidthOffset.Should().Be(0);
        c.TargetDetailIndex.Should().Be(1);
    }

    [Fact]
    public void T4b_ThicknessOnlyChange_CurrentIsThicker()
    {
        var segs = new[]
        {
            new SegLwt(8000, 400, 50),
            new SegLwt(16000, 400, 30),
        };
        var cuts = FlangeTransitionRules.BuildThicknessCuts(segs, BottomRatio);

        cuts.Should().HaveCount(1);
        var c = cuts[0];
        c.Decrease.Should().BeTrue();        // current (left, thicker) decreases going right
        c.Theoretical.Should().BeApproximately(8000, Tol);
        c.Start.Should().BeApproximately(8000, Tol);
        c.End.Should().BeApproximately(8160, Tol);
        c.TargetDetailIndex.Should().Be(0);  // cut belongs to current detail (the thicker one)
    }

    // ---------------------------------------------------------------
    // T5: ΔW + ΔT simultaneously → DetailBoundary uses max(W,T) length;
    //     ThicknessCutSpec.WidthOffset stores the difference
    // ---------------------------------------------------------------

    [Fact]
    public void T5_BothWidthAndThicknessChange_WidthDrivesLength()
    {
        // ΔW = 200 → widthLen = 200/2 * 8 = 800.
        // ΔT = 20 → thicknessLen = 20 * 8 = 160.
        // transitionLen = max(800, 160) = 800 → driven by width.
        // WidthOffset = 800 - 160 = 640 (cut shifted past joint by 640).
        var segs = new[]
        {
            new SegLwt(8000, 400, 30),
            new SegLwt(16000, 600, 50),
        };

        var details = FlangeTransitionRules.GroupDetails(segs, BottomRatio);
        var cuts = FlangeTransitionRules.BuildThicknessCuts(segs, BottomRatio);

        details.Should().HaveCount(2);
        AssertDetail(details[0], start: 0, end: 8000 - 800, w: 400, t: 30, ts: 0, te: 0);
        AssertDetail(details[1], start: 8000 - 800, end: 24000, w: 600, t: 50, ts: 800, te: 0);

        cuts.Should().HaveCount(1);
        var c = cuts[0];
        c.Decrease.Should().BeFalse();
        c.Theoretical.Should().BeApproximately(8000, Tol);
        // Next is thicker, widthOffset = 640. cutToX = 8000 - 640 = 7360. Start = 7360 - 160 = 7200. End = 7360.
        c.Start.Should().BeApproximately(7200, Tol);
        c.End.Should().BeApproximately(7360, Tol);
        c.WidthOffset.Should().BeApproximately(640, Tol);
    }

    [Fact]
    public void T5b_ThicknessDrivesLength_WhenLargerThanWidth()
    {
        // ΔW = 20 → widthLen = 20/2 * 8 = 80.
        // ΔT = 30 → thicknessLen = 30 * 8 = 240.
        // transitionLen = max(80, 240) = 240 → driven by thickness.
        // WidthOffset = 80 - 240 = -160 → cut shifted -160 from joint.
        var segs = new[]
        {
            new SegLwt(8000, 400, 30),
            new SegLwt(16000, 420, 60),
        };

        var cuts = FlangeTransitionRules.BuildThicknessCuts(segs, BottomRatio);

        cuts.Should().HaveCount(1);
        var c = cuts[0];
        c.WidthOffset.Should().BeApproximately(-160, Tol);
        // Decrease=false (next thicker). cutToX = 8000 - (-160) = 8160. Start = 8160 - 240 = 7920. End = 8160.
        c.Start.Should().BeApproximately(7920, Tol);
        c.End.Should().BeApproximately(8160, Tol);
    }

    // ---------------------------------------------------------------
    // T6: Cascade — width steps up twice
    // ---------------------------------------------------------------

    [Fact]
    public void T6_CascadeWidthUpUp()
    {
        // 3 segments, each 8m, W: 400 → 500 → 600.
        // Joint 0 (at 8000): ΔW=100, taper 400 onto next plate (500 wider).
        // Joint 1 (at 16000): ΔW=100, taper 400 onto next plate (600 wider).
        var segs = new[]
        {
            new SegLwt(8000, 400, 30),
            new SegLwt(8000, 500, 30),
            new SegLwt(8000, 600, 30),
        };

        var details = FlangeTransitionRules.GroupDetails(segs, BottomRatio);

        details.Should().HaveCount(3);
        AssertDetail(details[0], start: 0, end: 7600, w: 400, t: 30, ts: 0, te: 0);
        AssertDetail(details[1], start: 7600, end: 15600, w: 500, t: 30, ts: 400, te: 0);
        AssertDetail(details[2], start: 15600, end: 24000, w: 600, t: 30, ts: 400, te: 0);
    }

    // ---------------------------------------------------------------
    // T7: Thickness peak — thin, thick, thin
    // ---------------------------------------------------------------

    [Fact]
    public void T7_ThicknessPeak_TwoCutsBothOnThickPlate()
    {
        var segs = new[]
        {
            new SegLwt(8000, 400, 30),
            new SegLwt(8000, 400, 50),
            new SegLwt(8000, 400, 30),
        };

        var details = FlangeTransitionRules.GroupDetails(segs, BottomRatio);
        var cuts = FlangeTransitionRules.BuildThicknessCuts(segs, BottomRatio);

        details.Should().HaveCount(3);
        AssertDetail(details[0], start: 0, end: 7840, w: 400, t: 30, ts: 0, te: 0);
        AssertDetail(details[1], start: 7840, end: 16160, w: 400, t: 50, ts: 0, te: 0);
        AssertDetail(details[2], start: 16160, end: 24000, w: 400, t: 30, ts: 0, te: 0);

        cuts.Should().HaveCount(2);
        // Joint 0 (at 8000): next is thicker → Decrease=false, on detail[1].
        cuts[0].Decrease.Should().BeFalse();
        cuts[0].Start.Should().BeApproximately(7840, Tol);
        cuts[0].End.Should().BeApproximately(8000, Tol);
        cuts[0].TargetDetailIndex.Should().Be(1);
        // Joint 1 (at 16000): current is thicker → Decrease=true, on detail[1].
        cuts[1].Decrease.Should().BeTrue();
        cuts[1].Start.Should().BeApproximately(16000, Tol);
        cuts[1].End.Should().BeApproximately(16160, Tol);
        cuts[1].TargetDetailIndex.Should().Be(1);
    }

    // ---------------------------------------------------------------
    // T8: Symmetric thin-thick-thin with equal arms
    // ---------------------------------------------------------------

    [Fact]
    public void T8_SymmetricThickPlate()
    {
        // Symmetric: equal end segments. Both joints generate equivalent cuts (mirror).
        var segs = new[]
        {
            new SegLwt(6000, 400, 30),
            new SegLwt(12000, 400, 50),
            new SegLwt(6000, 400, 30),
        };
        var cuts = FlangeTransitionRules.BuildThicknessCuts(segs, BottomRatio);

        cuts.Should().HaveCount(2);
        // First cut: 6000-160 .. 6000 ? No — recheck. ΔT=20, taperLen=160. Next thicker → cut at cursor - taperLen .. cursor.
        // cursor after seg[0] = 6000. cutToX = 6000 - 0 = 6000. Start = 5840. End = 6000.
        cuts[0].Start.Should().BeApproximately(5840, Tol);
        cuts[0].End.Should().BeApproximately(6000, Tol);
        // Second cut at joint i=1, cursor after seg[1] = 18000. Current thicker → cutFromX = 18000. End = 18160.
        cuts[1].Start.Should().BeApproximately(18000, Tol);
        cuts[1].End.Should().BeApproximately(18160, Tol);
    }

    // ---------------------------------------------------------------
    // T9: stress-zone ratio dependence
    // ---------------------------------------------------------------

    [Fact]
    public void T9_RatioAffectsTransitionLength()
    {
        var segs = new[]
        {
            new SegLwt(8000, 400, 30),
            new SegLwt(16000, 400, 50),
        };
        // ratio=8 → taperLen = 160; ratio=4 → taperLen = 80.
        var cuts8 = FlangeTransitionRules.BuildThicknessCuts(segs, 8.0);
        var cuts4 = FlangeTransitionRules.BuildThicknessCuts(segs, 4.0);

        (cuts8[0].End - cuts8[0].Start).Should().BeApproximately(160, Tol);
        (cuts4[0].End - cuts4[0].Start).Should().BeApproximately(80, Tol);
    }

    // ---------------------------------------------------------------
    // T10: Empty / null / single guards
    // ---------------------------------------------------------------

    [Fact]
    public void T10_EmptyAndNullGuards()
    {
        FlangeTransitionRules.GroupDetails(new SegLwt[0], BottomRatio).Should().BeEmpty();
        FlangeTransitionRules.GroupDetails(null!, BottomRatio).Should().BeEmpty();
        FlangeTransitionRules.BuildThicknessCuts(new SegLwt[0], BottomRatio).Should().BeEmpty();
        FlangeTransitionRules.BuildThicknessCuts(null!, BottomRatio).Should().BeEmpty();
        FlangeTransitionRules.BuildThicknessCuts(new[] { new SegLwt(1000, 400, 30) }, BottomRatio).Should().BeEmpty();
        FlangeTransitionRules.BuildFlangeTransitionSpecs(new SegLwt[0], 8, 8).Should().BeEmpty();
        FlangeTransitionRules.BuildFlangeTransitionSpecs(null!, 8, 8).Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    // T11: Sub-epsilon change → not treated as transition
    // ---------------------------------------------------------------

    [Fact]
    public void T11_SubEpsilonChangeNotTransition()
    {
        // ΔW = 0.005 < 0.01 epsilon → merged as one detail.
        // Note: even though the change is sub-epsilon, GroupDetails walks
        // forward and replaces `current = next`, so the final detail's
        // Width = segs[last].W = 400.005 (not 400.000). This matches the
        // decomp; document via explicit expected value.
        var segs = new[]
        {
            new SegLwt(8000, 400.000, 30),
            new SegLwt(16000, 400.005, 30),
        };

        var details = FlangeTransitionRules.GroupDetails(segs, BottomRatio);
        var cuts = FlangeTransitionRules.BuildThicknessCuts(segs, BottomRatio);

        details.Should().HaveCount(1);
        AssertDetail(details[0], start: 0, end: 24000, w: 400.005, t: 30, ts: 0, te: 0);
        cuts.Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    // T12: Large transition (>10% axis) — still valid, clipped to segment
    // ---------------------------------------------------------------

    [Fact]
    public void T12_LargeTransition_ClippedBySpecBuilder()
    {
        // ΔT=200, ratio=8 → taperLen = 1600. With seg[0]=1500 long, the
        // transition would naively need 1600 mm, but cursor is at 1500.
        // BuildFlangeTransitionSpecs clips to seg span. Decompiled logic:
        // prevStart = 1500 - 1500 = 0, nextEnd = 1500 + 8000 = 9500.
        // Current (T=300) thicker. start = cursor = 1500, end = 3100.
        // 3100 <= 9500 → kept. start >= 0 → kept.
        // After clipping: 1500..3100.
        var segs = new[]
        {
            new SegLwt(1500, 400, 300),
            new SegLwt(8000, 400, 100),
        };
        var specs = FlangeTransitionRules.BuildFlangeTransitionSpecs(segs, BottomRatio, BottomRatio);

        specs.Should().HaveCount(1);
        specs[0].Start.Should().BeApproximately(1500, Tol);
        specs[0].End.Should().BeApproximately(3100, Tol);
        specs[0].Theoretical.Should().BeApproximately(1500, Tol);
    }

    [Fact]
    public void T12b_TooShortTransition_Filtered()
    {
        // ΔT=0.1 → taperLen = 0.8 < 1mm → spec filtered out by < 1.0 guard.
        var segs = new[]
        {
            new SegLwt(8000, 400, 30),
            new SegLwt(8000, 400, 30.1),
        };
        var specs = FlangeTransitionRules.BuildFlangeTransitionSpecs(segs, BottomRatio, BottomRatio);
        specs.Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------

    private static void AssertDetail(DetailBoundary actual, double start, double end, double w, double t, double ts, double te)
    {
        actual.Start.Should().BeApproximately(start, Tol);
        actual.End.Should().BeApproximately(end, Tol);
        actual.Width.Should().Be(w);
        actual.Thickness.Should().Be(t);
        actual.TransitionStart.Should().BeApproximately(ts, Tol);
        actual.TransitionEnd.Should().BeApproximately(te, Tol);
    }
}
