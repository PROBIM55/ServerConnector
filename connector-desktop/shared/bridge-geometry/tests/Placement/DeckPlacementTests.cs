using Platform.Bridge.Geometry;
using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Placement;
using FluentAssertions;
using Xunit;

namespace Platform.Bridge.Geometry.Tests.Placement;

public class DeckPlacementTests
{
    private const double Tol = 1e-9;

    [Fact]
    public void Symmetric_TwoSegments_PerSide()
    {
        // Symmetric деки: 2 сегмента слева, 2 справа. Slope=0 → плоская палуба.
        var bottomSegs = new[] { new SegLwt(24000, 400, 30) };
        var system = SystemFrame.Build(2000, HeightMode.ToTop, bottomSegs, bottomSegs);
        var left = new[] { new SlopeSeg(1500, 0), new SlopeSeg(2000, 0) };
        var right = new[] { new SlopeSeg(1500, 0), new SlopeSeg(2000, 0) };

        var strips = DeckPlacement.BuildSlopedStrips(left, right, 250, system, "B1");
        strips.Should().HaveCount(4);

        // Left strip 0: from y=0 to y=-1500, Z constant at 2000.
        strips[0].YStart.Should().Be(0);
        strips[0].YEnd.Should().Be(-1500);
        strips[0].ZStart.Should().Be(2000);
        strips[0].ZEnd.Should().Be(2000);
        strips[0].Name.Should().Be("B1_DECK_L01");

        // Left strip 1: from y=-1500 to y=-3500.
        strips[1].YStart.Should().Be(-1500);
        strips[1].YEnd.Should().Be(-3500);
        strips[1].Name.Should().Be("B1_DECK_L02");

        // Right strip 0: from y=0 to y=+1500.
        strips[2].YStart.Should().Be(0);
        strips[2].YEnd.Should().Be(1500);
        strips[2].Name.Should().Be("B1_DECK_R01");

        // Right strip 1: from y=+1500 to y=+3500.
        strips[3].YStart.Should().Be(1500);
        strips[3].YEnd.Should().Be(3500);
        strips[3].Name.Should().Be("B1_DECK_R02");
    }

    [Fact]
    public void Sloped_ZRisesAccordingToSlopePromille()
    {
        // Slope=25 промилле = 25mm rise per 1000mm run.
        // Strip L=1500, slope=25 → ΔZ = 1500 * 25 / 1000 = 37.5mm.
        var bottomSegs = new[] { new SegLwt(24000, 400, 30) };
        var system = SystemFrame.Build(2000, HeightMode.ToTop, bottomSegs, bottomSegs);
        var left = new[] { new SlopeSeg(1500, 25) };
        var right = new[] { new SlopeSeg(2000, -10) };

        var strips = DeckPlacement.BuildSlopedStrips(left, right, 250, system, "B1");
        strips.Should().HaveCount(2);

        // Left: y from 0 to -1500, z from 2000 to 2000+37.5=2037.5.
        strips[0].ZStart.Should().Be(2000);
        strips[0].ZEnd.Should().BeApproximately(2037.5, Tol);

        // Right: y from 0 to 2000, z from 2000 to 2000 + 2000*-10/1000 = 1980.
        strips[1].ZStart.Should().Be(2000);
        strips[1].ZEnd.Should().BeApproximately(1980, Tol);
    }

    [Fact]
    public void EmptySlopes_ReturnsEmpty()
    {
        var bottomSegs = new[] { new SegLwt(24000, 400, 30) };
        var system = SystemFrame.Build(2000, HeightMode.ToTop, bottomSegs, bottomSegs);
        var strips = DeckPlacement.BuildSlopedStrips(
            new SlopeSeg[0], new SlopeSeg[0], 250, system, "B1");
        strips.Should().BeEmpty();
    }
}
