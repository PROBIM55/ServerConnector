using Platform.Bridge.Geometry;
using Platform.Bridge.Geometry.Cuts;
using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Geometry3d;
using Platform.Bridge.Geometry.Placement;
using FluentAssertions;
using Xunit;

namespace Platform.Bridge.Geometry.Tests.Cuts;

/// <summary>
/// Тесты cutting-геометрии. Эталонные значения вычислены вручную из логики
/// FlangeCutters + decomp поведения. Изменение вершин = регрессия binary-parity.
/// </summary>
public class FlangeCuttersTests
{
    private const double BottomRatio = 8.0;
    private const double Tol = 1e-6;

    private static BeamFrame CanonicalBeam() =>
        BeamFrame.Build(new Vec3(0, 0, 0), new Vec3(24000, 0, 0));

    // ---------------------------------------------------------------
    // BuildBottomThicknessCutters
    // ---------------------------------------------------------------

    [Fact]
    public void Thickness_NoChange_NoCutters()
    {
        var segs = new[] { new SegLwt(24000, 400, 30) };
        var cutters = FlangeCutters.BuildBottomThicknessCutters(
            segs, BottomRatio, CanonicalBeam(), FlangeRef.BottomLocked, 1, "B1");
        cutters.Should().BeEmpty();
    }

    [Fact]
    public void Thickness_NextThicker_BottomLocked_CutterAtJointToEnd()
    {
        // ΔT=20, ratio=8, taperLen=160. Cut at 7840..8000 on plate index 1 (the thicker).
        // BOTTOM_LOCKED: zAtStart=ThinT=30 (start side has thin top), zAtEnd=ThickT=50.
        // (Decrease=false → current thinner, next thicker; cut on next plate, from x=7840 to x=8000,
        //  thickness rising from 30 to 50.)
        // zFar = max(30,50) + 100 = 150 (CutterExtent reduced from 5000 → 100 — visual
        // cutter no longer torchит 5 м над поясом, 100 мм достаточно для boolean cut).
        var segs = new[]
        {
            new SegLwt(8000, 400, 30),
            new SegLwt(16000, 400, 50),
        };
        var cutters = FlangeCutters.BuildBottomThicknessCutters(
            segs, BottomRatio, CanonicalBeam(), FlangeRef.BottomLocked, bottomPartCount: 2, "B1");

        cutters.Should().HaveCount(1);
        var c = cutters[0];
        c.TargetPartIndex.Should().Be(1);
        c.Name.Should().Be("B1_CUT_BOT_T_01");
        c.Thickness.Should().Be(600); // MaxW=400 + 200 margin
        c.Vertices.Should().HaveCount(4);

        AssertVec(c.Vertices[0], 7840, 0, 30);    // v1: Start, low (thin side)
        AssertVec(c.Vertices[1], 8000, 0, 50);    // v2: End, higher (thick side)
        AssertVec(c.Vertices[2], 8000, 0, 150);  // v3: End, far above
        AssertVec(c.Vertices[3], 7840, 0, 150);  // v4: Start, far above
    }

    [Fact]
    public void Thickness_TopLocked_NegativeZ()
    {
        // TOP_LOCKED: плита под baseline. Z значения отрицательные.
        // ΔT=20, BOTTOM(decrease=true). Current thicker, cut on current plate (idx 0).
        // zAtStart=-ThickT=-50, zAtEnd=-ThinT=-30, zFar=min(-50,-30)-100=-150.
        var segs = new[]
        {
            new SegLwt(8000, 400, 50),
            new SegLwt(16000, 400, 30),
        };
        var cutters = FlangeCutters.BuildBottomThicknessCutters(
            segs, BottomRatio, CanonicalBeam(), FlangeRef.TopLocked, bottomPartCount: 2, "B1");

        cutters.Should().HaveCount(1);
        var c = cutters[0];
        c.TargetPartIndex.Should().Be(0);
        // Decrease=true, cut at 8000..8160.
        AssertVec(c.Vertices[0], 8000, 0, -50);
        AssertVec(c.Vertices[1], 8160, 0, -30);
        AssertVec(c.Vertices[2], 8160, 0, -150);
        AssertVec(c.Vertices[3], 8000, 0, -150);
    }

    // ---------------------------------------------------------------
    // BuildBottomWidthCutters
    // ---------------------------------------------------------------

    [Fact]
    public void Width_NoChange_NoCutters()
    {
        var segs = new[] { new SegLwt(24000, 400, 30) };
        var cutters = FlangeCutters.BuildBottomWidthCutters(
            segs, BottomRatio, CanonicalBeam(), FlangeRef.BottomLocked, 1, "B1");
        cutters.Should().BeEmpty();
    }

    [Fact]
    public void Width_NextWider_TwoTriangleCutters_LeftAndRight()
    {
        // ΔW=100, ratio=8. widthTaperLen = 100/2 * 8 = 400. transitionLen=400.
        // Next plate (500) wider → ToNextForWide=true, target index = 1.
        // Theoretical=8000. xAtJoint = 8000 - 400 = 7600. xDirection=+1. xAtTip = 7600+400 = 8000.
        // BOTTOM_LOCKED + bottom flange: zBase = 0 (baseline at plate bottom).
        // maxHalfW = 500/2 = 250 (next is wider), minHalfW = 400/2 = 200.
        var segs = new[]
        {
            new SegLwt(8000, 400, 30),
            new SegLwt(16000, 500, 30),
        };
        var cutters = FlangeCutters.BuildBottomWidthCutters(
            segs, BottomRatio, CanonicalBeam(), FlangeRef.BottomLocked, bottomPartCount: 2, "B1");

        cutters.Should().HaveCount(2); // left + right
        var left = cutters[0];
        left.TargetPartIndex.Should().Be(1);
        left.Name.Should().Be("B1_CUT_BOT_W_01_L");
        left.Vertices.Should().HaveCount(3);
        // side=-1 в beam-local. Для канонической балки вдоль +X beam.Ey=(0,-1,0),
        // поэтому L cutter (side=-1) даёт +world-Y. R (side=+1) даёт -world-Y.
        // Поведение decomp идентично — naming L/R это beam-local "left/right".
        AssertVec(left.Vertices[0], 7600, 250, 0);
        AssertVec(left.Vertices[1], 7600, 200, 0);
        AssertVec(left.Vertices[2], 8000, 250, 0);

        var right = cutters[1];
        right.Name.Should().Be("B1_CUT_BOT_W_01_R");
        AssertVec(right.Vertices[0], 7600, -250, 0);
        AssertVec(right.Vertices[1], 7600, -200, 0);
        AssertVec(right.Vertices[2], 8000, -250, 0);
    }

    [Fact]
    public void Width_CurrentWider_CutterExtendsRight()
    {
        // Current (500) wider → ToNextForWide=false, target = current detail (0).
        // ΔW=100, widthTaperLen=400.
        // xAtJoint = 8000 + 400 = 8400. xDirection=-1. xAtTip = 8400 - 400 = 8000.
        var segs = new[]
        {
            new SegLwt(8000, 500, 30),
            new SegLwt(16000, 400, 30),
        };
        var cutters = FlangeCutters.BuildBottomWidthCutters(
            segs, BottomRatio, CanonicalBeam(), FlangeRef.BottomLocked, bottomPartCount: 2, "B1");

        cutters.Should().HaveCount(2);
        var left = cutters[0];
        left.TargetPartIndex.Should().Be(0);
        // side=-1 (beam-local L) → +world-Y, см. предыдущий тест.
        AssertVec(left.Vertices[0], 8400, 250, 0);
        AssertVec(left.Vertices[2], 8000, 250, 0);
    }

    // ---------------------------------------------------------------
    // Top flange cutters
    // ---------------------------------------------------------------

    [Fact]
    public void TopThickness_BaselineFromSystemFrame()
    {
        var bottomSegs = new[] { new SegLwt(24000, 400, 30) };
        var topSegs = new[]
        {
            new SegLwt(8000, 400, 30),
            new SegLwt(16000, 400, 50),
        };
        var system = SystemFrame.Build(2000, HeightMode.ToTop, bottomSegs, topSegs);
        // TopBaselineY=2000. TOP_LOCKED + top → плита под baseline, zAt = 2000 - T.
        var cutters = FlangeCutters.BuildTopThicknessCutters(
            topSegs, BottomRatio, CanonicalBeam(), system, FlangeRef.TopLocked, topPartCount: 2, "B1");

        cutters.Should().HaveCount(1);
        var c = cutters[0];
        // Decrease=false. zAtStart = 2000 - 30 = 1970, zAtEnd = 2000 - 50 = 1950.
        // zFar = min(1970,1950) - 100 = 1850.
        AssertVec(c.Vertices[0], 7840, 0, 1970);
        AssertVec(c.Vertices[1], 8000, 0, 1950);
        AssertVec(c.Vertices[2], 8000, 0, 1850);
        AssertVec(c.Vertices[3], 7840, 0, 1850);
        c.Name.Should().Be("B1_CUT_TOP_T_01");
    }

    // ---------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------

    private static void AssertVec(Vec3 actual, double x, double y, double z)
    {
        actual.X.Should().BeApproximately(x, Tol);
        actual.Y.Should().BeApproximately(y, Tol);
        actual.Z.Should().BeApproximately(z, Tol);
    }
}
