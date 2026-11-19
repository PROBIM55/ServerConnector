using Xunit;
using Connector.Desktop.Services;

namespace Connector.Desktop.Tests;

public sealed class WindowWorkAreaFitTests
{
    [Fact]
    public void Size_that_fits_is_kept_and_not_moved()
    {
        var fit = WindowWorkAreaFit.Fit(1180, 900, 920, 600, 0, 0, 1920, 1032, centre: true);
        Assert.Equal(new WindowWorkAreaFit.Placement(1180, 900, null, null), fit);
    }

    [Fact]
    public void Laptop_1080p_at_150_percent_gets_the_work_area_height_centred()
    {
        // 1920×1080 at 150 %: work area 1280×688 DIP.
        var fit = WindowWorkAreaFit.Fit(1180, 900, 920, 600, 0, 0, 1280, 688, centre: true);
        Assert.Equal(new WindowWorkAreaFit.Placement(1180, 688, 50, 0), fit);
    }

    [Fact]
    public void Work_area_smaller_than_the_minimum_keeps_the_title_bar_on_screen()
    {
        // 1366×768 at 150 %: ≈ 910×480 DIP, below MinWidth×MinHeight; the window starts at the work-area corner.
        var fit = WindowWorkAreaFit.Fit(1180, 900, 920, 600, 100, 20, 910, 480, centre: true);
        Assert.Equal(new WindowWorkAreaFit.Placement(920, 600, 100, 20), fit);
    }

    [Fact]
    public void Manual_placement_is_only_resized()
    {
        var fit = WindowWorkAreaFit.Fit(1500, 900, 920, 600, 0, 0, 1366, 728, centre: false);
        Assert.Equal(new WindowWorkAreaFit.Placement(1366, 728, null, null), fit);
    }
}
