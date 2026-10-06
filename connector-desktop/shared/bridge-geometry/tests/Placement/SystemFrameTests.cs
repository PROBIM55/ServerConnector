using Platform.Bridge.Geometry;
using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Placement;
using FluentAssertions;
using Xunit;

namespace Platform.Bridge.Geometry.Tests.Placement;

public class SystemFrameTests
{
    private static readonly SegLwt[] BottomSegs =
    {
        new SegLwt(24000, 400, 30),
    };

    private static readonly SegLwt[] TopSegs =
    {
        new SegLwt(24000, 400, 20),
    };

    [Fact]
    public void ToTop_MeasuresFromActualBottomFlangeTop()
    {
        var frame = SystemFrame.Build(2000, HeightMode.ToTop, BottomSegs, TopSegs, bottomRef: FlangeRef.TopLocked);
        frame.BottomBaselineY.Should().Be(0);
        frame.TopBaselineY.Should().Be(2000);
        frame.WebClearHeight.Should().Be(1980); // 2000 - 20 (top max T) - 0
    }

    [Fact]
    public void ToBottom_MeasuresFromActualBottomFlangeBottom()
    {
        var frame = SystemFrame.Build(2000, HeightMode.ToBottom, BottomSegs, TopSegs, bottomRef: FlangeRef.TopLocked);
        frame.BottomBaselineY.Should().Be(0);
        frame.TopBaselineY.Should().Be(1970);
        // Нижняя грань НП = -30; от неё до верха системы 1970 - (-30) = 2000.
        (frame.TopBaselineY - (-30)).Should().Be(2000);
        frame.WebClearHeight.Should().Be(1950);
    }

    [Theory]
    [InlineData(HeightMode.ToTop, 2030)]
    [InlineData(HeightMode.ToBottom, 2000)]
    public void HeightDatum_IsIndependentFromLockedBottomFlangeFace(HeightMode mode, double expectedTop)
    {
        var frame = SystemFrame.Build(2000, mode, BottomSegs, TopSegs, wallTiltRad: 38 * System.Math.PI / 180, bottomRef: FlangeRef.BottomLocked);
        frame.BottomBaselineY.Should().Be(0);
        frame.TopBaselineY.Should().Be(expectedTop);
    }

    [Theory]
    [InlineData(FlangeRef.BottomLocked, false, 0, 30, 15)]     // bottom flange BOTTOM_LOCKED: center = 0 + 30/2 = 15
    [InlineData(FlangeRef.TopLocked, false, 0, 30, -15)]       // bottom flange TOP_LOCKED: center = 0 - 30/2 = -15
    [InlineData(FlangeRef.BottomLocked, true, 2000, 20, 2010)] // top flange BOTTOM_LOCKED: center = 2000 + 20/2 = 2010
    [InlineData(FlangeRef.TopLocked, true, 2000, 20, 1990)]    // top flange TOP_LOCKED: center = 2000 - 20/2 = 1990
    public void PlateCenterY_RespectsRefAndFlange(FlangeRef flangeRef, bool isTop, double baselineY, double thickness, double expectedY)
    {
        // Build SystemFrame so that BottomBaselineY=0 (ToTop with 0 max bottom T) — but we want exact baseline. Set h, etc.
        var frame = new SystemFrame(BottomBaselineY: isTop ? 0 : baselineY, TopBaselineY: isTop ? baselineY : 0, WebClearHeight: 0);
        frame.PlateCenterY(thickness, flangeRef, isTop).Should().Be(expectedY);
    }
}
