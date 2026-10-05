using Platform.Bridge.Geometry;
using FluentAssertions;
using Xunit;

namespace Platform.Bridge.Geometry.Tests;

public class StressZoneTests
{
    [Fact]
    public void BottomTension_BottomRatioIsGentle_TopRatioIsSteep()
    {
        // Растянутый пояс (низ при положительном моменте) требует 1:8 перехода
        // для усталостного ресурса сварных швов. Сжатый — 1:4. Это normативный
        // СП-уклон, захардкожен и в плагине.
        var (bottom, top) = StressZone.BottomTension.GetRatios();
        bottom.Should().Be(8.0);
        top.Should().Be(4.0);
    }

    [Fact]
    public void TopTension_Swaps()
    {
        var (bottom, top) = StressZone.TopTension.GetRatios();
        bottom.Should().Be(4.0);
        top.Should().Be(8.0);
    }
}
