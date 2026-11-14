using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

/// <summary>The sliced AUTO-heading table (UI responsiveness) must give the same answers as the one-shot build.</summary>
public class SlicedWarmUpTests
{
    private static PanelLayout ThreePanels() => new()
    {
        Mode = PanelLayout.FixedMode,
        Facets = [new() { AzOffsetDeg = 0, NormalElevDeg = 90, Share = 1 },
                  new() { AzOffsetDeg = -30, NormalElevDeg = 0, Share = 1 },
                  new() { AzOffsetDeg = 30, NormalElevDeg = 0, Share = 1 }],
    };

    private static IlluminationSeries Series()
    {
        var n = 24 * 30;
        var az = Enumerable.Range(0, n).Select(i => (12.0 * i / 24) % 360).ToArray();
        var el = Enumerable.Range(0, n).Select(i => 2 + 3 * Math.Sin(i / 20.0)).ToArray();
        var sun = Enumerable.Range(0, n).Select(i => i % 50 < 40 ? 100 : 30).ToArray();
        return SolarGeometryTests.GeoSeries(sun, el, az);
    }

    [Fact]
    public async Task Sliced_build_matches_the_synchronous_build()
    {
        var layout = ThreePanels();
        var model = PanelModel.For(layout)!;
        var syncSeries = Series();
        var slicedSeries = Series();

        // budget 0 → yields before every heading row: the most sliced case
        await SolarGeometry.WarmUpAsync(slicedSeries, model, budgetMs: 0);

        foreach (var (start, steps) in new[] { (0, 24 * 7), (100, 24 * 7), (300, 24 * 14), (0, 24 * 30) })
        {
            Assert.Equal(SolarGeometry.BestHeading(syncSeries, start, steps, model),
                         SolarGeometry.BestHeading(slicedSeries, start, steps, model));
        }
    }

    [Fact]
    public async Task Warm_up_is_a_no_op_for_ideal_tracking_and_honours_cancellation()
    {
        await SolarGeometry.WarmUpAsync(Series(), null);                    // ideal tracking: nothing to build
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SolarGeometry.WarmUpAsync(Series(), PanelModel.For(ThreePanels()), budgetMs: 0, ct: cts.Token));
    }
}
