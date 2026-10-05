using System;
using Platform.Bridge.Geometry.Geometry3d;
using FluentAssertions;
using Xunit;

namespace Platform.Bridge.Geometry.Tests.Geometry3d;

public class Vec3Tests
{
    [Fact]
    public void AddSubtract_Componentwise()
    {
        var a = new Vec3(1, 2, 3);
        var b = new Vec3(10, 20, 30);
        (a + b).Should().Be(new Vec3(11, 22, 33));
        (b - a).Should().Be(new Vec3(9, 18, 27));
    }

    [Fact]
    public void Scale_BothSides()
    {
        var v = new Vec3(1, 2, 3);
        (v * 2).Should().Be(new Vec3(2, 4, 6));
        (3.0 * v).Should().Be(new Vec3(3, 6, 9));
    }

    [Fact]
    public void Length_Pythagorean()
    {
        new Vec3(3, 4, 0).Length().Should().BeApproximately(5, 1e-12);
        new Vec3(0, 0, 0).Length().Should().Be(0);
    }

    [Fact]
    public void Normalize_UnitLength()
    {
        var n = new Vec3(3, 4, 0).Normalize();
        n.Length().Should().BeApproximately(1, 1e-12);
        n.X.Should().BeApproximately(0.6, 1e-12);
        n.Y.Should().BeApproximately(0.8, 1e-12);
    }

    [Fact]
    public void Normalize_ZeroVector_Throws()
    {
        Action act = () => new Vec3(0, 0, 0).Normalize();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cross_RightHand()
    {
        Vec3.UnitX.Cross(Vec3.UnitY).Should().Be(Vec3.UnitZ);
        Vec3.UnitY.Cross(Vec3.UnitZ).Should().Be(Vec3.UnitX);
        Vec3.UnitZ.Cross(Vec3.UnitX).Should().Be(Vec3.UnitY);
    }

    [Fact]
    public void Dot_Examples()
    {
        Vec3.UnitX.Dot(Vec3.UnitX).Should().Be(1);
        Vec3.UnitX.Dot(Vec3.UnitY).Should().Be(0);
        new Vec3(1, 2, 3).Dot(new Vec3(4, 5, 6)).Should().Be(32);
    }
}
