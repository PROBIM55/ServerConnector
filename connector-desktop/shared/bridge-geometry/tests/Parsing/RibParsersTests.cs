using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Parsing;
using FluentAssertions;
using Xunit;

namespace Platform.Bridge.Geometry.Tests.Parsing;

public class RibParsersTests
{
    // ---------------------------------------------------------------
    // ParseWebLongRibs
    // ---------------------------------------------------------------

    [Fact]
    public void Long_Empty_ReturnsEmpty()
    {
        RibParsers.ParseWebLongRibs(null).Should().BeEmpty();
        RibParsers.ParseWebLongRibs("").Should().BeEmpty();
        RibParsers.ParseWebLongRibs("   ").Should().BeEmpty();
    }

    [Fact]
    public void Long_Single_HappyPath()
    {
        var specs = RibParsers.ParseWebLongRibs("left,0,400,500,16000,150,12");
        specs.Should().HaveCount(1);
        var s = specs[0];
        s.Side.Should().Be("left");
        s.BothSides.Should().BeFalse();
        s.Offset.Should().Be(400);
        s.Start.Should().Be(500);
        s.Length.Should().Be(16000);
        s.Height.Should().Be(150);
        s.Thickness.Should().Be(12);
    }

    [Fact]
    public void Long_BothSidesTrue_ParsedFlagOnly_NotMirrored()
    {
        // ParseWebLongRibs возвращает spec с BothSides=true; зеркалирование
        // делает CreateWebLongRibs на placement-этапе (E5c.2).
        var specs = RibParsers.ParseWebLongRibs("right,1,400,500,16000,150,12");
        specs.Should().HaveCount(1);
        specs[0].Side.Should().Be("right");
        specs[0].BothSides.Should().BeTrue();
    }

    [Fact]
    public void Long_Multiple_SemicolonSeparated()
    {
        var specs = RibParsers.ParseWebLongRibs(
            "left,0,400,0,12000,150,12;right,1,800,0,24000,200,16;left,yes,1200,5000,10000,120,10");
        specs.Should().HaveCount(3);
        specs[0].Offset.Should().Be(400);
        specs[1].Offset.Should().Be(800);
        specs[1].BothSides.Should().BeTrue();
        specs[2].BothSides.Should().BeTrue(); // "yes" → true
    }

    [Fact]
    public void Long_WrongFieldCount_Skipped()
    {
        // 6 fields вместо 7 → пропуск.
        var specs = RibParsers.ParseWebLongRibs("left,0,400,500,16000,150");
        specs.Should().BeEmpty();
    }

    [Fact]
    public void Long_NegativeOrZero_Filtered()
    {
        // Offset=0 → пропуск. Length<=0 → пропуск. Thickness<=0 → пропуск.
        // Start может быть 0 (это валидно — старт от 0-координаты).
        RibParsers.ParseWebLongRibs("left,0,0,500,16000,150,12").Should().BeEmpty();
        RibParsers.ParseWebLongRibs("left,0,400,500,0,150,12").Should().BeEmpty();
        RibParsers.ParseWebLongRibs("left,0,400,500,16000,150,0").Should().BeEmpty();
        RibParsers.ParseWebLongRibs("left,0,400,0,16000,150,12").Should().HaveCount(1); // Start=0 OK
    }

    [Fact]
    public void Long_BadSide_NormalizedToLeft()
    {
        var specs = RibParsers.ParseWebLongRibs("garbage,0,400,500,16000,150,12");
        specs[0].Side.Should().Be("left"); // Normalize.NormalizeSide fallback
    }

    [Fact]
    public void Long_MixValidAndInvalid_OnlyValidKept()
    {
        var specs = RibParsers.ParseWebLongRibs(
            "left,0,400,0,12000,150,12;invalid;right,0,0,0,16000,150,12;left,1,600,0,24000,200,16");
        specs.Should().HaveCount(2);
        specs[0].Offset.Should().Be(400);
        specs[1].Offset.Should().Be(600);
    }

    [Fact]
    public void Long_NoSegments_SegmentsNull()
    {
        var specs = RibParsers.ParseWebLongRibs("left,0,400,0,16000,150,12,P");
        specs.Should().HaveCount(1);
        specs[0].Orientation.Should().Be(RibOrientation.Perpendicular);
        specs[0].Segments.Should().BeNull();
    }

    [Fact]
    public void Long_WithSegments_Parsed()
    {
        // 9-е поле: участки "L:H:T~L:H:T~...".
        var specs = RibParsers.ParseWebLongRibs(
            "left,0,400,0,16000,150,12,P,4000:200:16~4000:160:14~8000:120:12");
        specs.Should().HaveCount(1);
        var s = specs[0];
        s.Segments.Should().NotBeNull();
        s.Segments!.Should().HaveCount(3);
        s.Segments![0].L.Should().Be(4000);
        s.Segments[0].H.Should().Be(200);
        s.Segments[0].T.Should().Be(16);
        s.Segments[2].L.Should().Be(8000);
        s.Segments[2].H.Should().Be(120);
        s.Segments[2].T.Should().Be(12);
    }

    [Fact]
    public void Long_SegmentsWithHorizontalOrient()
    {
        var specs = RibParsers.ParseWebLongRibs(
            "right,1,400,0,16000,150,12,H,8000:200:16~8000:150:12");
        specs[0].Orientation.Should().Be(RibOrientation.Horizontal);
        specs[0].Segments!.Should().HaveCount(2);
    }

    // ---------------------------------------------------------------
    // ParseWebTransRibs
    // ---------------------------------------------------------------

    [Fact]
    public void Trans_Empty_ReturnsEmpty()
    {
        RibParsers.ParseWebTransRibs(null).Should().BeEmpty();
        RibParsers.ParseWebTransRibs("").Should().BeEmpty();
    }

    [Fact]
    public void Trans_Single_HappyPath()
    {
        var specs = RibParsers.ParseWebTransRibs("left,1,3000,1800,16");
        specs.Should().HaveCount(1);
        var s = specs[0];
        s.Side.Should().Be("left");
        s.BothSides.Should().BeTrue();
        s.Step.Should().Be(3000);
        s.Height.Should().Be(1800);
        s.Thickness.Should().Be(16);
    }

    [Fact]
    public void Trans_Multiple()
    {
        var specs = RibParsers.ParseWebTransRibs(
            "left,1,3000,1800,16;right,0,2000,1500,12");
        specs.Should().HaveCount(2);
        specs[1].Step.Should().Be(2000);
        specs[1].BothSides.Should().BeFalse();
    }

    [Fact]
    public void Trans_WrongFieldCount_Skipped()
    {
        // Strict arity check (== 5): меньше → пропуск, больше → пропуск.
        RibParsers.ParseWebTransRibs("left,1,3000,1800").Should().BeEmpty();
        RibParsers.ParseWebTransRibs("left,1,3000,1800,16,extra").Should().BeEmpty();
    }

    [Fact]
    public void Trans_Negative_Filtered()
    {
        RibParsers.ParseWebTransRibs("left,1,0,1800,16").Should().BeEmpty();
        RibParsers.ParseWebTransRibs("left,1,3000,0,16").Should().BeEmpty();
        RibParsers.ParseWebTransRibs("left,1,3000,1800,0").Should().BeEmpty();
    }

    [Fact]
    public void Trans_BadSide_FallbackLeft()
    {
        var specs = RibParsers.ParseWebTransRibs("xyz,0,3000,1800,16");
        specs[0].Side.Should().Be("left");
    }

    [Fact]
    public void Trans_DecimalValuesAccepted()
    {
        // TryParseDoubleLenient допускает запятую вместо точки — но не
        // здесь, так как `,` уже разделитель полей. Проверяем что dot
        // работает.
        var specs = RibParsers.ParseWebTransRibs("left,0,3000.5,1800.25,16.1");
        specs[0].Step.Should().BeApproximately(3000.5, 1e-9);
        specs[0].Height.Should().BeApproximately(1800.25, 1e-9);
        specs[0].Thickness.Should().BeApproximately(16.1, 1e-9);
    }
}
