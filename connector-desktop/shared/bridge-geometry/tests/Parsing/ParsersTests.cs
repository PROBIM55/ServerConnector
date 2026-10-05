using System;
using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Parsing;
using FluentAssertions;
using Xunit;

namespace Platform.Bridge.Geometry.Tests.Parsing;

public class ParsersTests
{
    // ---------------------------------------------------------------
    // ParseLwtSegments
    // ---------------------------------------------------------------

    [Fact]
    public void ParseLwtSegments_Single()
    {
        var result = Parsers.ParseLwtSegments("8000,400,30", "flange");
        result.Should().ContainSingle()
            .Which.Should().Be(new SegLwt(8000, 400, 30));
    }

    [Fact]
    public void ParseLwtSegments_Multiple()
    {
        var result = Parsers.ParseLwtSegments("8000,400,30|8000,500,40|8000,400,30", "flange");
        result.Should().HaveCount(3);
        result[0].Should().Be(new SegLwt(8000, 400, 30));
        result[1].Should().Be(new SegLwt(8000, 500, 40));
        result[2].Should().Be(new SegLwt(8000, 400, 30));
    }

    [Fact]
    public void ParseLwtSegments_TrailingPipeIgnored()
    {
        var result = Parsers.ParseLwtSegments("8000,400,30|", "flange");
        result.Should().HaveCount(1);
    }

    [Fact]
    public void ParseLwtSegments_IntegerInputs()
    {
        var result = Parsers.ParseLwtSegments("100,200,30", "flange");
        result[0].Should().Be(new SegLwt(100, 200, 30));
    }

    [Fact]
    public void ParseLwtSegments_DecimalInputs()
    {
        var result = Parsers.ParseLwtSegments("8000.5,400.25,30.1", "flange");
        result[0].L.Should().BeApproximately(8000.5, 1e-9);
        result[0].W.Should().BeApproximately(400.25, 1e-9);
        result[0].T.Should().BeApproximately(30.1, 1e-9);
    }

    [Fact]
    public void ParseLwtSegments_WrongFieldCount_Throws()
    {
        Action act = () => Parsers.ParseLwtSegments("8000,400", "flange");
        act.Should().Throw<FormatException>().WithMessage("flange segment must be L,W,T");
    }

    [Fact]
    public void ParseLwtSegments_NotANumber_Throws()
    {
        Action act = () => Parsers.ParseLwtSegments("8000,XX,30", "flange");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParseLwtSegments_LabelEmbeddedInError()
    {
        Action act = () => Parsers.ParseLwtSegments("a,b", "web");
        act.Should().Throw<FormatException>().WithMessage("web segment must be L,W,T");
    }

    // ---------------------------------------------------------------
    // ParseSlopeSegs
    // ---------------------------------------------------------------

    [Fact]
    public void ParseSlopeSegs_Single()
    {
        var result = Parsers.ParseSlopeSegs("1500,25");
        result.Should().ContainSingle().Which.Should().Be(new SlopeSeg(1500, 25));
    }

    [Fact]
    public void ParseSlopeSegs_MultipleNegativeSlope()
    {
        var result = Parsers.ParseSlopeSegs("1500,25|2000,-15");
        result.Should().HaveCount(2);
        result[1].S.Should().Be(-15);
    }

    [Fact]
    public void ParseSlopeSegs_WrongFieldCount_Throws()
    {
        Action act = () => Parsers.ParseSlopeSegs("1500,25,5");
        act.Should().Throw<FormatException>().WithMessage("slope segment must be L,S");
    }

    // ---------------------------------------------------------------
    // ParseDouble / TryParseDoubleLenient
    // ---------------------------------------------------------------

    [Fact]
    public void ParseDouble_InvariantDecimalPoint()
    {
        Parsers.ParseDouble("1234.5").Should().BeApproximately(1234.5, 1e-9);
    }

    [Fact]
    public void ParseDouble_InvariantRejectsComma()
    {
        // double.Parse with InvariantCulture trims allowed group separators in some
        // .NET versions but never treats ',' as decimal. "12,5" parses as 125
        // (comma-as-thousands), not as 12.5. This asserts that behaviour so future
        // refactors don't silently change it.
        Parsers.ParseDouble("12,5").Should().Be(125);
    }

    [Theory]
    [InlineData("1234.5", true, 1234.5)]
    [InlineData("1234,5", true, 1234.5)] // lenient: ',' → '.'
    [InlineData("", false, 0.0)]
    [InlineData(null, false, 0.0)]
    [InlineData("abc", false, 0.0)]
    public void TryParseDoubleLenient_Cases(string? raw, bool expectedOk, double expectedValue)
    {
        var ok = Parsers.TryParseDoubleLenient(raw, out var v);
        ok.Should().Be(expectedOk);
        if (expectedOk)
            v.Should().BeApproximately(expectedValue, 1e-9);
    }

    // ---------------------------------------------------------------
    // ParsePair
    // ---------------------------------------------------------------

    [Fact]
    public void ParsePair_Ok()
    {
        var p = Parsers.ParsePair("3000,250", "deckW,deckT");
        p.Item1.Should().Be(3000);
        p.Item2.Should().Be(250);
    }

    [Fact]
    public void ParsePair_WrongArity_Throws()
    {
        Action act = () => Parsers.ParsePair("3000", "deckW,deckT");
        act.Should().Throw<FormatException>().WithMessage("deckW,deckT expected 2 values");
    }

    [Fact]
    public void ParsePair_TooManyValues_Throws()
    {
        Action act = () => Parsers.ParsePair("3000,250,1", "deckW,deckT");
        act.Should().Throw<FormatException>();
    }

    // ---------------------------------------------------------------
    // ParseDeckSlopes
    // ---------------------------------------------------------------

    [Fact]
    public void ParseDeckSlopes_Full()
    {
        var (deckT, left, right) = Parsers.ParseDeckSlopes("250^1500,25|2000,15^1500,25|2000,15");
        deckT.Should().Be(250);
        left.Should().HaveCount(2);
        right.Should().HaveCount(2);
        left[0].Should().Be(new SlopeSeg(1500, 25));
        right[1].Should().Be(new SlopeSeg(2000, 15));
    }

    [Fact]
    public void ParseDeckSlopes_WrongSeparatorCount_Throws()
    {
        Action act = () => Parsers.ParseDeckSlopes("250^1500,25");
        act.Should().Throw<FormatException>()
            .WithMessage("DECK_SLOPES format: deckT^L1,s1|...^R1,s1|...");
    }

    [Fact]
    public void ParseDeckSlopes_EmptySidesAllowed_LengthZero()
    {
        // Separator count is the only structural check; empty side lists parse
        // to zero-length arrays. Step4 then refuses (left.Length == 0) → ERROR.
        // Parser is intentionally permissive — semantic validation is later.
        var (deckT, left, right) = Parsers.ParseDeckSlopes("250^^");
        deckT.Should().Be(250);
        left.Should().BeEmpty();
        right.Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    // ParseDoubleSteps
    // ---------------------------------------------------------------

    [Fact]
    public void ParseDoubleSteps_Empty_ReturnsEmpty()
    {
        Parsers.ParseDoubleSteps("").Should().BeEmpty();
        Parsers.ParseDoubleSteps("   ").Should().BeEmpty();
        Parsers.ParseDoubleSteps(null).Should().BeEmpty();
    }

    [Fact]
    public void ParseDoubleSteps_Multi()
    {
        var s = Parsers.ParseDoubleSteps("1500|2000|1500");
        s.Should().Equal(1500.0, 2000.0, 1500.0);
    }

    // ---------------------------------------------------------------
    // ParseNamedArg
    // ---------------------------------------------------------------

    [Fact]
    public void ParseNamedArg_EqualsDelimiter()
    {
        var parts = new[] { "step4", "0,0,0", "24000,0,0", "h=2000" };
        Parsers.ParseNamedArg(parts, "h", 3).Should().Be("2000");
    }

    [Fact]
    public void ParseNamedArg_ColonDelimiter()
    {
        var parts = new[] { "cmd", "stressZone:TOP_TENSION" };
        Parsers.ParseNamedArg(parts, "stressZone", 1).Should().Be("TOP_TENSION");
    }

    [Fact]
    public void ParseNamedArg_CaseInsensitiveKey()
    {
        var parts = new[] { "cmd", "StressZone=BOTTOM_TENSION" };
        Parsers.ParseNamedArg(parts, "stresszone", 1).Should().Be("BOTTOM_TENSION");
    }

    [Fact]
    public void ParseNamedArg_NotFound_ReturnsNull()
    {
        var parts = new[] { "cmd", "h=2000" };
        Parsers.ParseNamedArg(parts, "stressZone", 1).Should().BeNull();
    }

    [Fact]
    public void ParseNamedArg_BeforeStartIndex_Ignored()
    {
        var parts = new[] { "h=2000", "cmd" };
        // startIndex = 1: don't look at parts[0]
        Parsers.ParseNamedArg(parts, "h", 1).Should().BeNull();
    }

    [Fact]
    public void ParseNamedArg_EmptyValue_ReturnsNull()
    {
        var parts = new[] { "cmd", "h=" };
        Parsers.ParseNamedArg(parts, "h", 1).Should().BeNull();
    }

    [Fact]
    public void ParseNamedArg_TolerantToBlanks()
    {
        var parts = new[] { "cmd", "", "  ", "topRef=BOTTOM_LOCKED" };
        Parsers.ParseNamedArg(parts, "topRef", 1).Should().Be("BOTTOM_LOCKED");
    }

    // ---------------------------------------------------------------
    // ParseBool
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("y", true)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParseBool_Cases(string? raw, bool expected)
    {
        Parsers.ParseBool(raw).Should().Be(expected);
    }

    // ---------------------------------------------------------------
    // TryParseComponentId
    // ---------------------------------------------------------------

    [Fact]
    public void TryParseComponentId_CmpidEquals()
    {
        var parts = new string[14];
        // null entries skipped by TryParseComponentId (IsNullOrWhiteSpace).
        parts[13] = "cmpid=12345";
        var id = Parsers.TryParseComponentId(parts, out var has);
        has.Should().BeTrue();
        id.Should().Be(12345);
    }

    [Fact]
    public void TryParseComponentId_ComponentIdColon()
    {
        var parts = new string[14];
        // null entries skipped by TryParseComponentId (IsNullOrWhiteSpace).
        parts[13] = "component_id:99";
        var id = Parsers.TryParseComponentId(parts, out var has);
        has.Should().BeTrue();
        id.Should().Be(99);
    }

    [Fact]
    public void TryParseComponentId_NotPresent()
    {
        var parts = new string[14];
        var id = Parsers.TryParseComponentId(parts, out var has);
        has.Should().BeFalse();
        id.Should().Be(0);
    }

    [Fact]
    public void TryParseComponentId_ShortArray()
    {
        var parts = new[] { "step4", "0,0,0" };
        var id = Parsers.TryParseComponentId(parts, out var has);
        has.Should().BeFalse();
        id.Should().Be(0);
    }

    [Fact]
    public void TryParseComponentId_InvalidNumberStillFlagsPresent()
    {
        // hasComponentIdArg=true сигнализирует "пользователь намеревался
        // указать modify-target". Невалидное число даёт id=0, но флаг true —
        // вызывающий код это интерпретирует как SKIP_INVALID_COMPONENT_ID
        // (см. decomp num4>0 vs hasComponentIdArg ветвление).
        var parts = new string[14];
        // null entries skipped by TryParseComponentId (IsNullOrWhiteSpace).
        parts[13] = "cmpid=NaN";
        var id = Parsers.TryParseComponentId(parts, out var has);
        has.Should().BeTrue();
        id.Should().Be(0);
    }
}
