using System;
using Platform.Bridge.Geometry;
using Platform.Bridge.Geometry.Parsing;
using FluentAssertions;
using Xunit;

namespace Platform.Bridge.Geometry.Tests.Parsing;

public class NormalizeTests
{
    // ---------------------------------------------------------------
    // NormalizeStressZone
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("BOTTOM_TENSION", StressZone.BottomTension)]
    [InlineData("bottom_tension", StressZone.BottomTension)]
    [InlineData("TOP_TENSION", StressZone.TopTension)]
    [InlineData("  top_tension  ", StressZone.TopTension)]
    [InlineData("", StressZone.BottomTension)]      // fallback
    [InlineData(null, StressZone.BottomTension)]    // fallback
    [InlineData("INVALID", StressZone.BottomTension)] // fallback
    public void NormalizeStressZone_Cases(string? raw, StressZone expected)
    {
        Normalize.NormalizeStressZone(raw).Should().Be(expected);
    }

    // ---------------------------------------------------------------
    // NormalizeFlangeRef
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("TOP_LOCKED", FlangeRef.TopLocked)]
    [InlineData("BOTTOM_LOCKED", FlangeRef.BottomLocked)]
    [InlineData("top_locked", FlangeRef.TopLocked)]
    [InlineData(null, FlangeRef.TopLocked)]   // default fallback
    [InlineData("", FlangeRef.TopLocked)]
    [InlineData("garbage", FlangeRef.TopLocked)]
    public void NormalizeFlangeRef_DefaultFallback(string? raw, FlangeRef expected)
    {
        Normalize.NormalizeFlangeRef(raw).Should().Be(expected);
    }

    [Fact]
    public void NormalizeFlangeRef_CustomFallback()
    {
        Normalize.NormalizeFlangeRef(null, FlangeRef.BottomLocked)
            .Should().Be(FlangeRef.BottomLocked);
        Normalize.NormalizeFlangeRef("garbage", FlangeRef.BottomLocked)
            .Should().Be(FlangeRef.BottomLocked);
    }

    // ---------------------------------------------------------------
    // NormalizeTopMode
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("TOP_FLANGE", TopMode.TopFlange)]
    [InlineData("DECK", TopMode.Deck)]
    [InlineData("DECK_SLOPES", TopMode.DeckSlopes)]
    [InlineData("  top_flange  ", TopMode.TopFlange)]
    public void NormalizeTopMode_Valid(string raw, TopMode expected)
    {
        Normalize.NormalizeTopMode(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ORTHOTROPIC_DECK")] // historical typo from Phase 0 doc — explicitly invalid now
    [InlineData("XYZ")]
    public void NormalizeTopMode_Invalid_Throws(string? raw)
    {
        Action act = () => Normalize.NormalizeTopMode(raw);
        act.Should().Throw<FormatException>()
            .WithMessage("topMode must be TOP_FLANGE or DECK or DECK_SLOPES");
    }

    // ---------------------------------------------------------------
    // NormalizeHeightMode
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("TO_TOP", HeightMode.ToTop)]
    [InlineData("TO_BOTTOM", HeightMode.ToBottom)]
    [InlineData(null, HeightMode.ToTop)]
    [InlineData("garbage", HeightMode.ToTop)]
    public void NormalizeHeightMode_Cases(string? raw, HeightMode expected)
    {
        Normalize.NormalizeHeightMode(raw).Should().Be(expected);
    }

    // ---------------------------------------------------------------
    // NormalizeSide
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("left", "left")]
    [InlineData("right", "right")]
    [InlineData("LEFT", "left")]
    [InlineData("R", "right")]
    [InlineData("L", "left")]
    [InlineData("", "left")]
    [InlineData(null, "left")]
    [InlineData("garbage", "left")]
    public void NormalizeSide_Cases(string? raw, string expected)
    {
        Normalize.NormalizeSide(raw).Should().Be(expected);
    }
}
