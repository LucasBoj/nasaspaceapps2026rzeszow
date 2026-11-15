using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

public class MonteCarloTests
{
    private static readonly MissionThresholds Thresholds = new(20, 0);

    /// <summary>Two days, Sun 3° high all the time, Earth 5° high: fully lit and connected at an open spot.</summary>
    private static IlluminationSeries Site()
    {
        var n = 48;
        var s = SolarGeometryTests.GeoSeries(Enumerable.Repeat(100, n).ToArray(), Enumerable.Repeat(3.0, n).ToArray(),
                                             Enumerable.Repeat(180.0, n).ToArray());
        s.EarthVisible = Enumerable.Repeat(1, n).ToArray();
        s.EarthElevCentiDeg = Enumerable.Repeat(500, n).ToArray();
        s.EarthAzCentiDeg = Enumerable.Repeat(0, n).ToArray();
        s.HorizonDeg = new double[360];
        return s;
    }

    private static readonly LanderInputs Lander = new(100, 200, 40, 20, ParamKind.Derived);

    [Fact]
    public void Wilson_interval()
    {
        var (lo, hi) = MonteCarlo.Wilson(87, 100);
        Assert.InRange(lo, 0.79, 0.80);
        Assert.InRange(hi, 0.92, 0.93);
        Assert.Equal((0.0, 1.0), MonteCarlo.Wilson(0, 0));
    }

    [Fact]
    public void Without_uncertainty_every_sample_repeats_the_nominal_verdict()
    {
        var site = Site();
        var nominal = MissionSimulator.Run(site, Lander, Fixtures.T0, 1, Thresholds);
        Assert.Equal(Verdict.Feasible, nominal.Verdict);
        var r = MonteCarlo.Run(site, null, Lander, nominal, Thresholds, new UncertaintySpec(0, 0, 0, 50, 1))!;
        Assert.Equal(1.0, r.PFeasible);
        Assert.Equal(1, r.LandingPoints);
        Assert.False(r.HeadingVaried);   // ideal tracking: no heading
    }

    [Fact]
    public void Landing_points_behind_a_wall_lower_the_probability()
    {
        // Target and one open point are lit; one point behind a 40° wall is dark (Sun at 3°) → the 200 Wh battery
        // cannot last 24 h on 20 W, so 1 of 3 equally likely landing points fails: P(FEASIBLE) ≈ 2/3.
        var site = Site();
        var nominal = MissionSimulator.Run(site, Lander, Fixtures.T0, 1, Thresholds);
        var ellipse = new LandingEllipseData
        {
            RadiusM = 120, SpacingM = 60,
            Points =
            [
                new EllipsePoint { E = 60, HorizonCentiDeg = Enumerable.Repeat(4000, 360).ToArray() },
                new EllipsePoint { E = -60, HorizonCentiDeg = Enumerable.Repeat(-2000, 360).ToArray() },
                new EllipsePoint { E = 600, HorizonCentiDeg = Enumerable.Repeat(-2000, 360).ToArray() },   // outside the radius
            ],
        };
        var r = MonteCarlo.Run(site, ellipse, Lander, nominal, Thresholds, new UncertaintySpec(100, 0, 0, 600, 7))!;
        Assert.Equal(3, r.LandingPoints);
        Assert.InRange(r.PFeasible, 0.6, 0.73);
        Assert.Equal(r.Samples, r.Feasible + r.Marginal + r.NotFeasible);
        Assert.True(r.CiLow < r.PFeasible && r.PFeasible < r.CiHigh);
    }

    [Fact]
    public void Load_uncertainty_around_break_even_gives_about_half()
    {
        // Darkness for 24 h, 20 W survival load, battery sized so load × 1.0 just survives: half the draws above 1 fail.
        var n = 48;
        var dark = SolarGeometryTests.GeoSeries(new int[n], Enumerable.Repeat(-5.0, n).ToArray(), new double[n]);
        dark.EarthVisible = Enumerable.Repeat(1, n).ToArray();
        var lander = new LanderInputs(100, 20 * 24 + 1, 40, 20, ParamKind.Derived);
        var nominal = MissionSimulator.Run(dark, lander, Fixtures.T0, 1, new MissionThresholds(0, 0));
        Assert.Equal(Verdict.Feasible, nominal.Verdict);
        var r = MonteCarlo.Run(dark, null, lander, nominal, new MissionThresholds(0, 0), new UncertaintySpec(0, 0.2, 0, 800, 3))!;
        Assert.InRange(r.PFeasible, 0.42, 0.58);
    }

    [Fact]
    public void Same_seed_same_result_and_heading_varies_only_with_fixed_panels()
    {
        var site = Site();
        var wall = new LanderInputs(100, 200, 40, 20, ParamKind.Derived,
            new PanelLayout { Mode = PanelLayout.FixedMode, Facets = [new PanelFacet { AzOffsetDeg = 0, NormalElevDeg = 0, Share = 1 }] });
        var nominal = MissionSimulator.Run(site, wall, Fixtures.T0, 1, Thresholds);
        var spec = new UncertaintySpec(0, 0.2, 15, 200, 42);
        var a = MonteCarlo.Run(site, null, wall, nominal, Thresholds, spec)!;
        var b = MonteCarlo.Run(site, null, wall, nominal, Thresholds, spec)!;
        Assert.Equal(a, b);
        Assert.True(a.HeadingVaried);
    }
}

public class UncertaintySentenceTests
{
    [Fact]
    public void Plain_and_analyst_wording()
    {
        var spec = new UncertaintySpec(400, 0.2, 15, 400, 1);
        var r = new MonteCarloResult(spec, 400, 348, 40, 12, 137, true, 0.83, 0.90);
        Assert.Equal("The mission works in 348 of 400 simulated landings — touching down anywhere within 400 m of the target, " +
                     "power use 20% higher or lower, the lander turned slightly (up to 15°).",
                     VerdictText.UncertaintySentence(r, analyst: false));
        Assert.DoesNotContain("heading", VerdictText.UncertaintySentence(r, analyst: false));   // SIMPLE: plain words
        Assert.Contains("off its planned heading", VerdictText.UncertaintySentence(r, analyst: true));
        Assert.Contains("FEASIBLE 87% (95% interval 83–90%), MARGINAL 10%, NOT FEASIBLE 3%", VerdictText.UncertaintySentence(r, analyst: true));
        // Ideal tracking and no ellipse: only the load was sampled.
        var onlyLoad = r with { LandingPoints = 1, HeadingVaried = false };
        Assert.Equal("The mission works in 348 of 400 simulated landings — power use 20% higher or lower.",
                     VerdictText.UncertaintySentence(onlyLoad, analyst: false));
    }
}
