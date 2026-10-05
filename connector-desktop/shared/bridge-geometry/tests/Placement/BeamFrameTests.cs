using System;
using Platform.Bridge.Geometry.Geometry3d;
using Platform.Bridge.Geometry.Placement;
using FluentAssertions;
using Xunit;

namespace Platform.Bridge.Geometry.Tests.Placement;

public class BeamFrameTests
{
    [Fact]
    public void Horizontal_AlongX_StandardBasis()
    {
        // Beam from origin going along +X: ex=(1,0,0), ey=(1,0,0)×(0,0,1) = (0,-1,0), ez=ey×ex=(0,0,1)... actually let's compute.
        // ex × Z = (1,0,0) × (0,0,1) = (0·1 - 0·0, 0·0 - 1·1, 1·0 - 0·0) = (0, -1, 0). So ey = (0, -1, 0).
        // ez = ey × ex = (0,-1,0) × (1,0,0) = (-1·0 - 0·0, 0·1 - 0·0, 0·0 - (-1)·1) = (0, 0, 1).
        // Good — ez points up.
        var f = BeamFrame.Build(new Vec3(0, 0, 0), new Vec3(24000, 0, 0));
        f.AxisLength.Should().BeApproximately(24000, 1e-9);
        f.Ex.Should().Be(new Vec3(1, 0, 0));
        f.Ey.Should().Be(new Vec3(0, -1, 0));
        f.Ez.Should().Be(new Vec3(0, 0, 1));
    }

    [Fact]
    public void Diagonal_HorizontalBeam_EzStillUp()
    {
        // Beam going 45° in XY plane: ex = (sqrt(2)/2, sqrt(2)/2, 0).
        // ex × world-up: horizontal in XY plane perpendicular to ex.
        // ez = ey × ex must still point along world +Z (it's horizontal beam).
        var f = BeamFrame.Build(new Vec3(0, 0, 0), new Vec3(10000, 10000, 0));
        f.Ez.X.Should().BeApproximately(0, 1e-9);
        f.Ez.Y.Should().BeApproximately(0, 1e-9);
        f.Ez.Z.Should().BeApproximately(1, 1e-9);
    }

    [Fact]
    public void VerticalBeam_FallbackEy()
    {
        // ex = +Z. ex × world-up = (0,0,1)×(0,0,1) = 0 → fallback ey = (0,1,0).
        var f = BeamFrame.Build(new Vec3(0, 0, 0), new Vec3(0, 0, 5000));
        f.Ex.Should().Be(Vec3.UnitZ);
        f.Ey.Should().Be(Vec3.UnitY);
        // ez = ey × ex = (0,1,0) × (0,0,1) = (1, 0, 0).
        f.Ez.Should().Be(Vec3.UnitX);
    }

    [Fact]
    public void AxisPoint_LinearInterp()
    {
        var f = BeamFrame.Build(new Vec3(100, 200, 300), new Vec3(24100, 200, 300));
        f.AxisPoint(0).Should().Be(new Vec3(100, 200, 300));
        f.AxisPoint(12000).Should().Be(new Vec3(12100, 200, 300));
        f.AxisPoint(24000).Should().Be(new Vec3(24100, 200, 300));
    }

    [Fact]
    public void ZeroLengthAxis_Throws()
    {
        Action act = () => BeamFrame.Build(new Vec3(0, 0, 0), new Vec3(0, 0, 0));
        act.Should().Throw<InvalidOperationException>();
    }
}
