using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

public class WindowOptimizerTests
{
    private static readonly LanderInputs Lander = new(100, 200, 40, 20, ParamKind.Sourced);
    private static readonly MissionThresholds Thresholds = new(20, 40);

    [Fact]
    public void Score_formula_on_hand_computed_mission()
    {
        // Same case as MissionSimulatorTests: min SoC 50 %, comms 50 %, lit 37.5 %
        // energy: produced 3 h × 100 W = 300 Wh, used 3 × 40 + 5 × 20 = 220 Wh → surplus = 300/220 − 1 = 0.3636…
        // score = 40 × 0.50 + 25 × 0.50 + 15 × 0.375 + 20 × 0.3636… = 20 + 12.5 + 5.625 + 7.2727… = 45.3977…
        var series = Fixtures.Series([100, 100, 100, 0, 0, 0, 0, 0], [1, 1, 1, 1, 0, 0, 0, 0]);
        var r = MissionSimulator.Run(series, Lander, Fixtures.T0, 8.0 / 24, Thresholds);
        Assert.Equal(300, r.EnergyProducedWh, 9);
        Assert.Equal(220, r.EnergyLoadWh, 9);
        Assert.Equal(20 + 12.5 + 5.625 + 20 * (300.0 / 220 - 1), WindowOptimizer.Score(r), 9);
    }

    [Fact]
    public void Surplus_separates_missions_whose_battery_never_drops()
    {
        // Both stay at 100 % SoC with full comms and light — the old formula scored both 100.
        var sunny = Fixtures.Series(Fixtures.Repeat([100], 24), Enumerable.Repeat(1, 24).ToArray());
        var thin = MissionSimulator.Run(sunny, Lander with { DayLoadW = 90 }, Fixtures.T0, 1, Thresholds);   // 100 W vs 90 W
        var rich = MissionSimulator.Run(sunny, Lander with { DayLoadW = 40 }, Fixtures.T0, 1, Thresholds);   // 100 W vs 40 W
        Assert.Equal(100, thin.MinSocPct, 9);
        Assert.Equal(100, rich.MinSocPct, 9);
        Assert.Equal(80 + 20 * (100.0 / 90 - 1), WindowOptimizer.Score(thin), 9);
        Assert.Equal(100, WindowOptimizer.Score(rich), 9);                 // surplus clamps at 1 (≥ 2× the need)
        Assert.True(WindowOptimizer.Score(rich) > WindowOptimizer.Score(thin));
    }

    [Fact]
    public async Task Heatmap_carries_per_cell_metrics()
    {
        var sunny = Fixtures.Series(Fixtures.Repeat([100], 48), Enumerable.Repeat(1, 48).ToArray());
        var result = await WindowOptimizer.RunAsync([new("s", "S", sunny)], Lander, Fixtures.T0, Fixtures.T0.AddDays(5), 1, Thresholds);
        Assert.Equal(2, result.Days);
        Assert.Equal(100, result.SunPct[0, 0], 9);
        Assert.Equal(100, result.CommsPct[0, 0], 9);
        Assert.Equal(2.5, result.EnergyRatio[0, 0], 9);                    // 100 W produced / 40 W used
        Assert.Equal(100, result.MinSocPct[0, 0], 9);
        Assert.True(double.IsNaN(result.HeadingDeg[0, 0]));                 // ideal tracking: no heading
        Assert.Equal(1, result.PanelEfficiency[0, 0], 9);
    }

    [Fact]
    public void Not_feasible_scores_zero_and_survivors_at_least_one()
    {
        var dark = Fixtures.Series(new int[48], new int[48]);
        var dead = MissionSimulator.Run(dark, Lander with { BatteryWh = 10 }, Fixtures.T0, 1, Thresholds);
        Assert.Equal(Verdict.NotFeasible, dead.Verdict);
        Assert.Equal(0, WindowOptimizer.Score(dead));

        // Survives with almost nothing: 24 h × 20 W = 480 Wh from 481 Wh, no comms, no light.
        var barely = MissionSimulator.Run(dark, Lander with { BatteryWh = 481 }, Fixtures.T0, 1, Thresholds);
        Assert.True(barely.Survived);
        Assert.Equal(1, WindowOptimizer.Score(barely), 9);
    }

    [Fact]
    public void Landing_days_are_clipped_so_the_mission_fits()
    {
        // 5 days of data, 2-day mission → latest landing day is day 3 (index 3 → days 0..3 = 4 days)
        var series = Fixtures.Series(new int[120], new int[120]);
        var (first, days) = WindowOptimizer.LandingDays(series, Fixtures.T0.AddDays(-10), Fixtures.T0.AddDays(30), 2);
        Assert.Equal(Fixtures.T0, first);
        Assert.Equal(4, days);
    }

    [Fact]
    public async Task Optimizer_finds_the_best_site_and_day()
    {
        // Site A: always dark → small battery dies. Site B: dark on day 0, sunny from day 1 on.
        var dark = new int[24 * 5];
        var b = new int[24 * 5];
        for (var i = 24; i < b.Length; i++) b[i] = 100;
        var allEarth = Enumerable.Repeat(1, 24 * 5).ToArray();
        var sites = new List<OptimizerSite>
        {
            new("a", "Site A", Fixtures.Series(dark, allEarth)),
            new("b", "Site B", Fixtures.Series(b, allEarth)),
        };
        var lander = Lander with { BatteryWh = 100 };   // 100 Wh / 20 W = 5 h of darkness

        var progress = new List<double>();
        var result = await WindowOptimizer.RunAsync(sites, lander, Fixtures.T0, Fixtures.T0.AddDays(10), 1, Thresholds,
            new Collect(progress));

        Assert.Equal(5, result.Days);                      // 5 days of data, 1-day mission → landing days 0..4 (day 4 ends exactly at the data end)
        Assert.All(Enumerable.Range(0, 5), d => Assert.Equal(0, result.Scores[0, d]));
        Assert.Equal(0, result.Scores[1, 0]);              // site B day 0 is dark → dies
        Assert.Equal(1, result.BestSite);
        Assert.Equal(1, result.BestDay);                   // first full-sun day (ties keep the earliest)
        Assert.Equal(100, result.Scores[1, 1], 9);         // full battery, full comms, full light
        Assert.Equal(1, result.BestDayFor(1));
        Assert.Equal(-1, result.BestDayFor(0));
        Assert.Equal(1.0, progress[^1]);
    }

    [Fact]
    public async Task Ties_prefer_the_earliest_landing_day_not_list_order()
    {
        // Site A is perfect from day 2, site B from day 1 → both score 100; B lands sooner.
        var allEarth = Enumerable.Repeat(1, 24 * 5).ToArray();
        int[] SunFrom(int day) => Enumerable.Range(0, 24 * 5).Select(i => i >= day * 24 ? 100 : 0).ToArray();
        var sites = new List<OptimizerSite>
        {
            new("a", "Site A", Fixtures.Series(SunFrom(2), allEarth)),
            new("b", "Site B", Fixtures.Series(SunFrom(1), allEarth)),
        };
        var result = await WindowOptimizer.RunAsync(sites, Lander with { BatteryWh = 100 }, Fixtures.T0, Fixtures.T0.AddDays(10), 1, Thresholds);
        Assert.Equal((1, 1), (result.BestSite, result.BestDay));
    }

    private sealed class Collect(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }
}
