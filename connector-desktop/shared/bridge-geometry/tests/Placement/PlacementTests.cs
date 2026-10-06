using Platform.Bridge.Geometry;
using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Geometry3d;
using Platform.Bridge.Geometry.Placement;
using Platform.Bridge.Geometry.Rules;
using FluentAssertions;
using Xunit;

namespace Platform.Bridge.Geometry.Tests.Placement;

public class PlacementTests
{
    /// <summary>
    /// Канонический сценарий: балка 24m по +X, h=2000, нижний пояс 1 сегмент
    /// 400×30, верхний 400×20, BOTTOM_LOCKED + ToTop. Одна плита на пояс.
    /// </summary>
    [Fact]
    public void Canonical24m_OnePlateBottom_Centered()
    {
        var beam = BeamFrame.Build(new Vec3(0, 0, 0), new Vec3(24000, 0, 0));
        var bottomSegs = new[] { new SegLwt(24000, 400, 30) };
        var topSegs = new[] { new SegLwt(24000, 400, 20) };
        var system = SystemFrame.Build(2000, HeightMode.ToTop, bottomSegs, topSegs);
        var details = FlangeTransitionRules.GroupDetails(bottomSegs, 8.0);

        var plates = BeamPlacement.PlaceFlangePlates(
            details, beam, system, FlangeRef.BottomLocked, isTopFlange: false,
            material: "S355", namePrefix: "B1");

        plates.Should().HaveCount(1);
        var p = plates[0];
        p.Start.Should().Be(new Vec3(0, 0, 15));     // bottom flange center 15mm above baseline (T/2)
        p.End.Should().Be(new Vec3(24000, 0, 15));
        p.Width.Should().Be(400);
        p.Thickness.Should().Be(30);
        p.Material.Should().Be("S355");
        p.Name.Should().Be("B1_FLG_01");
        p.ClassId.Should().Be("3");
        p.ExtrudeAxis.Should().Be(beam.Ey);
        p.NormalAxis.Should().Be(beam.Ez);
    }

    [Fact]
    public void Canonical24m_TopFlange_TopLocked()
    {
        var beam = BeamFrame.Build(new Vec3(0, 0, 0), new Vec3(24000, 0, 0));
        var bottomSegs = new[] { new SegLwt(24000, 400, 30) };
        var topSegs = new[] { new SegLwt(24000, 400, 20) };
        var system = SystemFrame.Build(2000, HeightMode.ToTop, bottomSegs, topSegs);
        var details = FlangeTransitionRules.GroupDetails(topSegs, 4.0);

        var plates = BeamPlacement.PlaceFlangePlates(
            details, beam, system, FlangeRef.TopLocked, isTopFlange: true,
            material: "S355", namePrefix: "B1");

        plates.Should().HaveCount(1);
        var p = plates[0];
        // Top baseline = 2000, top-locked → center = 2000 - 20/2 = 1990.
        p.Start.Z.Should().Be(1990);
        p.End.Z.Should().Be(1990);
        p.Name.Should().Be("B1_TOP_01");
    }

    [Fact]
    public void ThreeSegmentCascade_ThreePlates_CorrectXSpans()
    {
        var beam = BeamFrame.Build(new Vec3(0, 0, 0), new Vec3(24000, 0, 0));
        var bottomSegs = new[]
        {
            new SegLwt(8000, 400, 30),
            new SegLwt(8000, 500, 30),
            new SegLwt(8000, 600, 30),
        };
        var topSegs = new[] { new SegLwt(24000, 400, 20) };
        var system = SystemFrame.Build(2000, HeightMode.ToTop, bottomSegs, topSegs);
        var details = FlangeTransitionRules.GroupDetails(bottomSegs, 8.0);

        var plates = BeamPlacement.PlaceFlangePlates(
            details, beam, system, FlangeRef.BottomLocked, isTopFlange: false,
            material: "S355", namePrefix: "B1");

        plates.Should().HaveCount(3);
        // Expected from T6 in FlangeTransitionRulesTests: [0..7600], [7600..15600], [15600..24000].
        plates[0].Start.X.Should().Be(0);
        plates[0].End.X.Should().Be(7600);
        plates[0].Width.Should().Be(400);
        plates[1].Start.X.Should().Be(7600);
        plates[1].End.X.Should().Be(15600);
        plates[1].Width.Should().Be(500);
        plates[2].Start.X.Should().Be(15600);
        plates[2].End.X.Should().Be(24000);
        plates[2].Width.Should().Be(600);
        // Naming counter increments.
        plates[0].Name.Should().Be("B1_FLG_01");
        plates[1].Name.Should().Be("B1_FLG_02");
        plates[2].Name.Should().Be("B1_FLG_03");
    }

    [Fact]
    public void OffsetBeam_OriginPropagatesToWorldCoords()
    {
        // Beam starts at (1000, 500, 0). Same canonical input — plates shifted.
        var beam = BeamFrame.Build(new Vec3(1000, 500, 0), new Vec3(25000, 500, 0));
        var bottomSegs = new[] { new SegLwt(24000, 400, 30) };
        var system = SystemFrame.Build(2000, HeightMode.ToTop, bottomSegs, bottomSegs);
        var details = FlangeTransitionRules.GroupDetails(bottomSegs, 8.0);

        var plates = BeamPlacement.PlaceFlangePlates(
            details, beam, system, FlangeRef.BottomLocked, isTopFlange: false,
            material: "S355", namePrefix: "B1");

        plates[0].Start.X.Should().Be(1000);
        plates[0].End.X.Should().Be(25000);
        plates[0].Start.Y.Should().Be(500);
        plates[0].Start.Z.Should().Be(15);
    }

    // ---------- Flange orientation (C — 042 §3 / П-3) ----------

    [Fact]
    public void FlangeTilt_Default_HorizontalEquivalent()
    {
        // flangeTiltRad=0 → результат идентичен прежнему API. Регрессионный
        // sanity-check, чтобы default-параметр не ломал старые места.
        var beam = BeamFrame.Build(new Vec3(0, 0, 0), new Vec3(24000, 0, 0));
        var bottomSegs = new[] { new SegLwt(24000, 400, 30) };
        var topSegs = new[] { new SegLwt(24000, 400, 20) };
        var system = SystemFrame.Build(2000, HeightMode.ToTop, bottomSegs, topSegs);
        var details = FlangeTransitionRules.GroupDetails(bottomSegs, 8.0);

        var plates = BeamPlacement.PlaceFlangePlates(
            details, beam, system, FlangeRef.BottomLocked, isTopFlange: false,
            material: "S355", namePrefix: "B1");
        var rotated = BeamPlacement.PlaceFlangePlates(
            details, beam, system, FlangeRef.BottomLocked, isTopFlange: false,
            material: "S355", namePrefix: "B1", classId: "3", flangeTiltRad: 0.0);

        plates.Should().HaveCount(1);
        rotated.Should().HaveCount(1);
        rotated[0].Start.X.Should().BeApproximately(plates[0].Start.X, 1e-9);
        rotated[0].Start.Y.Should().BeApproximately(plates[0].Start.Y, 1e-9);
        rotated[0].Start.Z.Should().BeApproximately(plates[0].Start.Z, 1e-9);
        rotated[0].ExtrudeAxis.X.Should().BeApproximately(plates[0].ExtrudeAxis.X, 1e-9);
        rotated[0].ExtrudeAxis.Y.Should().BeApproximately(plates[0].ExtrudeAxis.Y, 1e-9);
        rotated[0].ExtrudeAxis.Z.Should().BeApproximately(plates[0].ExtrudeAxis.Z, 1e-9);
    }

    [Fact]
    public void FlangeTilt_Bottom_RotatesAxesAndCenterAroundBaseline()
    {
        // I-girder, wallTiltDeg=10°, BOTTOM_LOCKED + ToTop.
        //   baselineY = system.BottomBaselineY = 0 (ToTop + BOTTOM_LOCKED).
        //   centerY = 15 (thickness/2 over baseline). centerYLocal = 15.
        // После rotation вокруг Ex by -tiltRad (right-hand convention):
        //   centerEy_local =  sin(tilt) · centerYLocal
        //   centerEz_local =  cos(tilt) · centerYLocal  (+ baselineY=0)
        var beam = BeamFrame.Build(new Vec3(0, 0, 0), new Vec3(24000, 0, 0));
        var bottomSegs = new[] { new SegLwt(24000, 400, 30) };
        var topSegs = new[] { new SegLwt(24000, 400, 20) };
        var system = SystemFrame.Build(2000, HeightMode.ToTop, bottomSegs, topSegs);
        var details = FlangeTransitionRules.GroupDetails(bottomSegs, 8.0);
        var tiltRad = 10.0 * System.Math.PI / 180.0;

        var plates = BeamPlacement.PlaceFlangePlates(
            details, beam, system, FlangeRef.BottomLocked, isTopFlange: false,
            material: "S355", namePrefix: "B1", classId: "3", flangeTiltRad: tiltRad);

        var p = plates[0];
        var expectedEy = System.Math.Sin(tiltRad) * 15.0;
        var expectedEz = System.Math.Cos(tiltRad) * 15.0;
        var centerOffset = p.Start - beam.LocalPoint(0, 0, 0);
        centerOffset.Dot(beam.Ex).Should().BeApproximately(0, 1e-9);
        centerOffset.Dot(beam.Ey).Should().BeApproximately(expectedEy, 1e-9);
        centerOffset.Dot(beam.Ez).Should().BeApproximately(expectedEz, 1e-9);
        p.ExtrudeAxis.Dot(beam.Ey).Should().BeApproximately(System.Math.Cos(tiltRad), 1e-9);
        p.ExtrudeAxis.Dot(beam.Ez).Should().BeApproximately(-System.Math.Sin(tiltRad), 1e-9);
        p.NormalAxis.Dot(beam.Ey).Should().BeApproximately(System.Math.Sin(tiltRad), 1e-9);
        p.NormalAxis.Dot(beam.Ez).Should().BeApproximately(System.Math.Cos(tiltRad), 1e-9);
        p.ExtrudeAxis.Dot(p.NormalAxis).Should().BeApproximately(0, 1e-9);
        p.ExtrudeAxis.Length().Should().BeApproximately(1, 1e-9);
        p.NormalAxis.Length().Should().BeApproximately(1, 1e-9);
    }

    [Fact]
    public void FlangeTilt_Top_PivotIsTopBaseline()
    {
        // Top flange при tilt: pivot = system.TopBaselineY = 2000.
        // centerY = 2000 - 20/2 = 1990, centerYLocal = -10.
        var beam = BeamFrame.Build(new Vec3(0, 0, 0), new Vec3(24000, 0, 0));
        var bottomSegs = new[] { new SegLwt(24000, 400, 30) };
        var topSegs = new[] { new SegLwt(24000, 400, 20) };
        var system = SystemFrame.Build(2000, HeightMode.ToTop, bottomSegs, topSegs);
        var details = FlangeTransitionRules.GroupDetails(topSegs, 4.0);
        var tiltRad = 8.0 * System.Math.PI / 180.0;

        var plates = BeamPlacement.PlaceFlangePlates(
            details, beam, system, FlangeRef.TopLocked, isTopFlange: true,
            material: "S355", namePrefix: "B1", classId: "3", flangeTiltRad: tiltRad);

        var p = plates[0];
        var centerOffset = p.Start - beam.LocalPoint(0, 0, 0);
        centerOffset.Dot(beam.Ey).Should().BeApproximately(-System.Math.Sin(tiltRad) * 10.0, 1e-9);
        centerOffset.Dot(beam.Ez).Should().BeApproximately(2000 - System.Math.Cos(tiltRad) * 10.0, 1e-9);
        p.NormalAxis.Dot(beam.Ey).Should().BeApproximately(System.Math.Sin(tiltRad), 1e-9);
        p.NormalAxis.Dot(beam.Ez).Should().BeApproximately(System.Math.Cos(tiltRad), 1e-9);
    }
}
