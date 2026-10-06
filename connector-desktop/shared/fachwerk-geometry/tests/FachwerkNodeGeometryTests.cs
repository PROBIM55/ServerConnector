using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace Platform.Fachwerk.Geometry.Tests;

public sealed class FachwerkNodeGeometryTests
{
    [Fact]
    public void Build_520Preset_ProducesNineStablePartsAndFiveHoleRows()
    {
        var spec = FachwerkNodeGeometry.Build(Input(520));

        spec.BottomElevationMm.Should().Be(22110);
        spec.ContourParts.Should().HaveCount(5);
        spec.BeamParts.Should().HaveCount(4);
        spec.Roles.Should().BeEquivalentTo(
            "main-web", "top-plate", "bottom-plate",
            "side-plate-left", "side-plate-right",
            "diagonal-left", "diagonal-right",
            "closure-left", "closure-right");
        spec.Holes.Select(static hole => hole.Center.Z).Should().Equal(430, 345, 260, 175, 90);
        spec.Holes.Should().OnlyContain(static hole => hole.DiameterMm == 24);
        spec.ColumnFits.Should().BeEquivalentTo(new[]
        {
            new FachwerkColumnFitSpec("upper-inner-flange", "inner-flange", 22630, true),
            new FachwerkColumnFitSpec("lower-inner-flange", "inner-flange", 22110, false),
            new FachwerkColumnFitSpec("upper-inner-web", "inner-web", 22630, true),
            new FachwerkColumnFitSpec("lower-inner-web", "inner-web", 22110, false),
            new FachwerkColumnFitSpec("upper-outer-web", "outer-web", 22630, true),
            new FachwerkColumnFitSpec("lower-outer-web", "outer-web", 22110, false),
        });
        spec.Signature.Should().HaveLength(32);
    }

    [Fact]
    public void Build_400Preset_UsesFourRowsAndFiftyFiveMillimeterBottomCenterOffset()
    {
        var spec = FachwerkNodeGeometry.Build(Input(400));

        spec.BottomElevationMm.Should().Be(22230);
        spec.Holes.Should().HaveCount(4);
        spec.Holes.Select(static hole => hole.Center.Z).Should().Equal(310, 225, 140, 55);
        spec.Holes[spec.Holes.Count - 1].Center.Z.Should().Be(55);
        spec.ColumnFits.Where(static fit => !fit.KeepAbove)
            .Should().OnlyContain(static fit => fit.ElevationMm == 22230);
    }

    [Fact]
    public void Build_MirroredSection_UsesTheSameRolesAndMirrorsFreeEdge()
    {
        var regular = FachwerkNodeGeometry.Build(Input(520));
        var mirroredInput = Input(520, mirrored: true);
        var mirrored = FachwerkNodeGeometry.Build(mirroredInput);

        regular.Roles.Should().Equal(mirrored.Roles);
        regular.ContourParts.Single(static part => part.Role == "main-web").Points[0].X.Should().Be(-20);
        mirrored.ContourParts.Single(static part => part.Role == "main-web").Points[0].X.Should().Be(20);
        regular.Holes[0].Center.X.Should().Be(22.5);
        mirrored.Holes[0].Center.X.Should().Be(-22.5);
    }

    [Fact]
    public void Build_IsDeterministicAndRejectsAnUnknownHeightPreset()
    {
        FachwerkNodeGeometry.Build(Input(520)).Signature
            .Should().Be(FachwerkNodeGeometry.Build(Input(520)).Signature);

        Action action = () => FachwerkNodeGeometry.Build(Input(450));
        action.Should().Throw<ArgumentException>().WithMessage("*520 mm and 400 mm*");
    }

    [Fact]
    public void Frame_IsOrthonormalAndSideSignOnlyMirrorsNormal()
    {
        var normal = new FachwerkNodeFrame(
            new FachwerkPoint3(100, 200, 300),
            new FachwerkVector3(3, 4, 7));
        var mirrored = new FachwerkNodeFrame(
            new FachwerkPoint3(100, 200, 300),
            new FachwerkVector3(3, 4, 7),
            -1);

        normal.AxisX.Z.Should().Be(0);
        FachwerkVector3.Dot(normal.AxisX, normal.AxisN).Should().BeApproximately(0, 1e-12);
        mirrored.AxisX.Should().Be(normal.AxisX);
        mirrored.AxisN.Should().Be(normal.AxisN * -1);
        normal.ToGlobal(new FachwerkLocalPoint(10, 20, 30)).Z.Should().Be(330);
    }

    [Fact]
    public void ColumnContext_RequiresSevenDistinctSemanticParts()
    {
        var outer = Part("outer", "outer-flange");
        var context = new FachwerkColumnContext(
            "owner", "SF1", outer,
            Part("if-u", "inner-flange"), Part("if-l", "inner-flange"),
            Part("iw-u", "inner-web"), Part("iw-l", "inner-web"),
            Part("ow-u", "outer-web"), Part("ow-l", "outer-web"));

        context.Parts.Should().HaveCount(7);

        Action duplicate = () => new FachwerkColumnContext(
            "owner", "SF1", outer,
            Part("if-u", "inner-flange"), Part("if-l", "inner-flange"),
            Part("iw-u", "inner-web"), Part("iw-l", "inner-web"),
            Part("ow-u", "outer-web"), Part("outer", "outer-web"));
        duplicate.Should().Throw<ArgumentException>().WithMessage("*distinct object*");
    }

    private static FachwerkColumnPartRef Part(string id, string role) => new(id, role, "POLYBEAM", 1);

    private static FachwerkNodeInput Input(double height, bool mirrored = false)
    {
        var top = 22630d;
        var bottom = top - height;
        return new FachwerkNodeInput
        {
            NodeId = "SF1/node/01",
            TopElevationMm = top,
            HeightMm = height,
            Frame = new FachwerkNodeFrame(
                new FachwerkPoint3(1000, 2000, bottom),
                new FachwerkVector3(1, 0, 0)),
            TopInnerFlangeCenterXmm = mirrored ? -300 : 300,
            BottomInnerFlangeCenterXmm = mirrored ? -200 : 200,
            OuterFlangeSamples = new[]
            {
                new FachwerkSectionSample(bottom, mirrored ? -600 : 600),
                new FachwerkSectionSample(bottom + height / 2, mirrored ? -650 : 650),
                new FachwerkSectionSample(top, mirrored ? -700 : 700),
            },
        };
    }
}
