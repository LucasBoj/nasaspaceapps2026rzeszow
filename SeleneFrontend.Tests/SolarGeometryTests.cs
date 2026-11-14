using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

public class SolarGeometryTests
{
    private static double Rad(double d) => d * Math.PI / 180;

    private static PanelLayout Layout(params (double Az, double Elev, double Share)[] facets) => new()
    {
        Mode = PanelLayout.FixedMode,
        Facets = facets.Select(f => new PanelFacet { AzOffsetDeg = f.Az, NormalElevDeg = f.Elev, Share = f.Share }).ToList(),
    };

    /// <summary>Series with a constant Earth and per-step Sun fraction, elevation and azimuth (degrees).</summary>
    internal static IlluminationSeries GeoSeries(int[] sun, double[] elDeg, double[] azDeg) => new()
    {
        SiteId = "geo",
        Start = Fixtures.T0,
        StepHours = 1,
        Count = sun.Length,
        SunFraction = sun,
        SunElevCentiDeg = elDeg.Select(e => (int)Math.Round(e * 100)).ToArray(),
        SunAzCentiDeg = azDeg.Select(a => (int)Math.Round(a * 100)).ToArray(),
        EarthVisible = sun.Select(_ => 1).ToArray(),
    };

    [Fact]
    public void Single_vertical_panel_follows_the_lambert_cosine()
    {
        var m = PanelModel.For(Layout((0, 0, 1)))!;
        Assert.Equal(1, m.Normalization, 9);                                    // best at el 0, facing the Sun
        Assert.Equal(1, SolarGeometry.Factor(m, 0, 0, 0), 9);                  // Sun straight ahead
        Assert.Equal(0.5, SolarGeometry.Factor(m, 0, 60, 0), 9);               // 60° to the side → cos 60°
        Assert.Equal(0, SolarGeometry.Factor(m, 0, 180, 0), 9);                // behind the panel → 0, not negative
        Assert.Equal(Math.Cos(Rad(5)), SolarGeometry.Factor(m, 5, 0, 0), 9);   // Sun 5° high → cos 5°
        Assert.Equal(1, SolarGeometry.Factor(m, 0, 250, 250), 9);              // heading turns the panel with the lander
    }

    [Fact]
    public void Top_deck_panel_gets_the_sine_of_the_sun_elevation()
    {
        var m = PanelModel.For(Layout((0, 90, 1)))!;
        Assert.Equal(Math.Sin(Rad(10)), m.Normalization, 9);                   // K at the top of the polar band
        Assert.Equal(Math.Sin(Rad(5)) / Math.Sin(Rad(10)), SolarGeometry.Factor(m, 5, 123, 0), 9);
    }

    [Fact]
    public void Three_facet_layout_normalises_to_its_best_sun_direction()
    {
        // Nova-C-like: deck + two vertical walls at ±30°, equal shares.
        var m = PanelModel.For(Layout((0, 90, 1), (-30, 0, 1), (30, 0, 1)))!;
        var expectedK = (Math.Sin(Rad(10)) + 2 * Math.Cos(Rad(10)) * Math.Cos(Rad(30))) / 3;
        Assert.Equal(expectedK, m.Normalization, 6);
        Assert.Equal(1, SolarGeometry.Factor(m, 10, 0, 0), 6);
        Assert.True(SolarGeometry.Factor(m, 5, 90, 0) < SolarGeometry.Factor(m, 5, 0, 0));
    }

    [Fact]
    public void Ideal_or_empty_layouts_have_no_model()
    {
        Assert.Null(PanelModel.For(null));
        Assert.Null(PanelModel.For(new PanelLayout { Mode = PanelLayout.IdealMode }));
        Assert.Null(PanelModel.For(new PanelLayout { Mode = PanelLayout.FixedMode }));
    }

    [Fact]
    public void Auto_heading_points_a_single_panel_at_a_fixed_sun()
    {
        var n = 48;
        var s = GeoSeries(Enumerable.Repeat(100, n).ToArray(), Enumerable.Repeat(3.0, n).ToArray(), Enumerable.Repeat(123.0, n).ToArray());
        var m = PanelModel.For(Layout((0, 0, 1)))!;
        var h = SolarGeometry.BestHeading(s, 0, n, m);
        Assert.InRange(h, 121, 125);
    }

    [Theory]
    [InlineData(0, 180)]     // Sun sweeps half the horizon (a polar lunar "day" seen from a 14-day mission)
    [InlineData(300, 420)]   // across north (wraps through 360°)
    [InlineData(0, 359)]     // a full circle
    public void Auto_heading_is_within_one_percent_of_a_brute_force_sweep(double azFrom, double azTo)
    {
        var n = 24 * 14;
        var az = Enumerable.Range(0, n).Select(i => (azFrom + (azTo - azFrom) * i / (n - 1)) % 360).ToArray();
        var el = Enumerable.Range(0, n).Select(i => 2 + 3 * Math.Sin(i / 20.0)).ToArray();
        var sun = Enumerable.Range(0, n).Select(i => i % 50 < 40 ? 100 : 30).ToArray();
        var s = GeoSeries(sun, el, az);

        foreach (var layout in new[] { Layout((0, 0, 1)), Layout((0, 90, 1), (-30, 0, 1), (30, 0, 1)), Layout((0, 90, 1), (-90, 0, 1), (90, 0, 1)) })
        {
            var m = PanelModel.For(layout)!;
            var best = Enumerable.Range(0, 360).Max(h => SolarGeometry.MissionEnergy(s, 0, n, m, h));
            var auto = SolarGeometry.MissionEnergy(s, 0, n, m, SolarGeometry.BestHeading(s, 0, n, m));
            Assert.True(auto >= 0.99 * best, $"auto {auto:0.00} vs brute force {best:0.00}");
        }
    }

    [Fact]
    public void All_dark_mission_returns_heading_zero()
    {
        var s = GeoSeries(new int[24], new double[24], new double[24]);
        Assert.Equal(0, SolarGeometry.BestHeading(s, 0, 24, PanelModel.For(Layout((0, 0, 1)))!));
    }
}
