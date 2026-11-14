using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

public class SensitivityAnalysisTests
{
    // 3 h full sun, 5 h dark; solar 100 W, battery 200 Wh, day 40 W, survival 20 W.
    // Nominal: SoC ends at 100 Wh (50%) → FEASIBLE with a 20% battery limit.
    // Survival load × f drains 5 × 20f Wh in the dark: min SoC = (200 − 100 f) / 200.
    //   f = 0.8 → 60%, f = 1.2 → 40%; still ≥ 20% until f = 1.6 (break-even, min SoC exactly 20%).
    private static readonly int[] Sun = [100, 100, 100, 0, 0, 0, 0, 0];
    private static readonly int[] Earth = [1, 1, 1, 1, 0, 0, 0, 0];
    private static readonly LanderInputs Lander = new(100, 200, 40, 20, ParamKind.Derived);
    private static readonly MissionThresholds Thresholds = new(MinBatteryPct: 20, MinCommsPct: 40);
    private const double EightHours = 8.0 / 24.0;

    private static (IlluminationSeries, SimulationResult) Nominal(LanderInputs lander, MissionThresholds? t = null)
    {
        var series = Fixtures.Series(Sun, Earth);
        return (series, MissionSimulator.Run(series, lander, Fixtures.T0, EightHours, t ?? Thresholds));
    }

    [Fact]
    public void Scales_day_and_survival_load_together()
    {
        var scaled = SensitivityAnalysis.ScaleLoads(Lander, 1.2);
        Assert.Equal(48, scaled.DayLoadW, 9);
        Assert.Equal(24, scaled.SurvivalLoadW, 9);
        Assert.Equal(Lander.SolarArrayW, scaled.SolarArrayW);
        Assert.Equal(Lander.BatteryWh, scaled.BatteryWh);
    }

    [Fact]
    public void Hand_computed_plus_minus_twenty_percent_and_break_even()
    {
        var (series, nominal) = Nominal(Lander);
        var s = SensitivityAnalysis.ForLoad(series, Lander, nominal, Thresholds);

        Assert.Equal(Verdict.Feasible, s.Nominal);
        Assert.Equal(Verdict.Feasible, s.Low);
        Assert.Equal(Verdict.Feasible, s.High);
        Assert.True(s.Robust);
        Assert.Equal(60, s.MinSocLowPct, 6);
        Assert.Equal(50, s.MinSocNominalPct, 6);
        Assert.Equal(40, s.MinSocHighPct, 6);
        Assert.NotNull(s.FeasibleUpToFactor);
        Assert.InRange(s.FeasibleUpToFactor!.Value, 1.595, 1.6 + 1e-9);
    }

    [Fact]
    public void Verdict_that_flips_within_twenty_percent_is_not_robust()
    {
        // Battery limit 45%: nominal 50% → FEASIBLE, +20% → 40% → MARGINAL. Break-even at f = 1.1.
        var t = new MissionThresholds(MinBatteryPct: 45, MinCommsPct: 40);
        var (series, nominal) = Nominal(Lander, t);
        var s = SensitivityAnalysis.ForLoad(series, Lander, nominal, t);

        Assert.Equal(Verdict.Feasible, s.Nominal);
        Assert.Equal(Verdict.Marginal, s.High);
        Assert.False(s.Robust);
        Assert.InRange(s.FeasibleUpToFactor!.Value, 1.095, 1.1 + 1e-9);
    }

    [Fact]
    public void Not_feasible_mission_reports_the_load_cut_it_needs()
    {
        // Battery 60 Wh: nominal drains 100 Wh in the dark → depleted. Feasible when 100 f ≤ 60 − 12 → f ≤ 0.48.
        var lander = Lander with { BatteryWh = 60 };
        var (series, nominal) = Nominal(lander);
        var s = SensitivityAnalysis.ForLoad(series, lander, nominal, Thresholds);

        Assert.Equal(Verdict.NotFeasible, s.Nominal);
        Assert.InRange(s.FeasibleUpToFactor!.Value, 0.475, 0.48 + 1e-9);
    }

    [Fact]
    public void Comms_shortfall_is_never_feasible_at_any_load()
    {
        var t = new MissionThresholds(MinBatteryPct: 20, MinCommsPct: 90);   // Earth only 50%
        var (series, nominal) = Nominal(Lander, t);
        var s = SensitivityAnalysis.ForLoad(series, Lander, nominal, t);

        Assert.Equal(Verdict.Marginal, s.Nominal);
        Assert.Null(s.FeasibleUpToFactor);
    }

    [Fact]
    public void Huge_margin_is_capped_at_the_search_range()
    {
        var lander = Lander with { BatteryWh = 10_000 };
        var (series, nominal) = Nominal(lander);
        var s = SensitivityAnalysis.ForLoad(series, lander, nominal, Thresholds);
        Assert.Equal(SensitivityAnalysis.MaxLoadFactor, s.FeasibleUpToFactor);
    }

    [Fact]
    public void Nasa_near_range_helpers()
    {
        var c = new NasaSiteCheck { NasaSunPct = 69.8, NasaSunRangePct = [42.7, 74.3] };
        Assert.Equal(42.7, c.NasaSunMinNearPct, 9);
        Assert.Equal(74.3, c.NasaSunMaxNearPct, 9);
        Assert.Equal(27.1, c.NearLossPp, 9);
        Assert.True(double.IsNaN(new NasaSiteCheck { NasaSunPct = 50 }.NearLossPp));
    }
}
