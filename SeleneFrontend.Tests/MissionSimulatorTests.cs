using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

internal static class Fixtures
{
    public static readonly DateTime T0 = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static IlluminationSeries Series(int[] sun, int[] earth, DateTime? start = null) => new()
    {
        SiteId = "test",
        Start = start ?? T0,
        StepHours = 1,
        Count = sun.Length,
        SunFraction = sun,
        EarthVisible = earth,
    };

    /// <summary>Repeat a pattern to build a longer series.</summary>
    public static int[] Repeat(int[] pattern, int times) => Enumerable.Repeat(pattern, times).SelectMany(x => x).ToArray();
}

public class MissionSimulatorTests
{
    // Hand-computed case: 3 h of full sun, then 5 h of darkness.
    //   lander: solar 100 W, battery 200 Wh, day load 40 W, survival load 20 W
    //   sun   = 100 100 100   0   0   0   0   0
    //   SoC   = 200 200 200 180 160 140 120 100 Wh   (+60 Wh/h clamped at full; −20 Wh/h in the dark)
    //   SoC % = 100 100 100  90  80  70  60  50
    //   Earth = 1 1 1 1 0 0 0 0  → 50 %
    private static readonly int[] Sun = [100, 100, 100, 0, 0, 0, 0, 0];
    private static readonly int[] Earth = [1, 1, 1, 1, 0, 0, 0, 0];
    private static readonly LanderInputs Lander = new(100, 200, 40, 20, ParamKind.Derived);
    private static readonly MissionThresholds Thresholds = new(MinBatteryPct: 20, MinCommsPct: 40);
    private const double EightHours = 8.0 / 24.0;

    [Fact]
    public void Hand_computed_state_of_charge()
    {
        var r = MissionSimulator.Run(Fixtures.Series(Sun, Earth), Lander, Fixtures.T0, EightHours, Thresholds);

        Assert.Equal(8, r.Steps);
        Assert.Equal(new double[] { 100, 100, 100, 90, 80, 70, 60, 50 }, r.SocPct, new ToleranceComparer(1e-9));
        Assert.Equal(50, r.MinSocPct, 9);
        Assert.Equal(7, r.MinSocStep);
        Assert.Equal(50, r.CommsPct, 9);
        Assert.Equal(37.5, r.SunPct, 9);
        Assert.Equal(5, r.LongestDarkH, 9);
        Assert.Equal(0, r.HoursBelowThreshold, 9);
        Assert.True(r.Survived);
        Assert.Equal(Verdict.Feasible, r.Verdict);
        Assert.Equal("battery never below 50%, Earth visible 50% of the time", r.Reason);
        Assert.Equal(ParamKind.Derived, r.BasedOn);
    }

    [Fact]
    public void Partial_sun_produces_power_but_uses_survival_load()
    {
        // sunFraction 40 (< 50) → production 40 W, load = survival 20 W → +20 Wh from 100 Wh
        var lander = Lander with { BatteryWh = 200 };
        var series = Fixtures.Series([0, 0, 0, 0, 0, 40], [0, 0, 0, 0, 0, 0]);
        var r = MissionSimulator.Run(series, lander, Fixtures.T0, 6.0 / 24, Thresholds);
        // 200 → 180 → 160 → 140 → 120 → 100 → 120
        Assert.Equal(60, r.SocPct[5], 9);
    }

    [Fact]
    public void Detects_battery_depletion_in_darkness()
    {
        // battery 50 Wh: 50, 50, 50 | 30, 10, −10 → depleted at step 5 after 3 h of darkness (day 1)
        var lander = Lander with { BatteryWh = 50 };
        var r = MissionSimulator.Run(Fixtures.Series(Sun, Earth), lander, Fixtures.T0, EightHours, Thresholds);

        Assert.False(r.Survived);
        Assert.Equal(5, r.DepletedAtStep);
        Assert.Equal(0, r.MinSocPct, 9);
        Assert.Equal(Verdict.NotFeasible, r.Verdict);
        Assert.Equal("battery depleted after 3 h of darkness on day 1", r.Reason);
    }

    [Fact]
    public void Detects_daytime_overload()
    {
        // Day load 150 W > 100 W solar: 200 → 150 → 100 → 50 → 0 at step 3 in full sun.
        var lander = Lander with { DayLoadW = 150 };
        var series = Fixtures.Series([100, 100, 100, 100, 100], [1, 1, 1, 1, 1]);
        var r = MissionSimulator.Run(series, lander, Fixtures.T0, 5.0 / 24, Thresholds);

        Assert.Equal(Verdict.NotFeasible, r.Verdict);
        Assert.Equal(3, r.DepletedAtStep);
        Assert.Contains("daytime load exceeds solar output", r.Reason);
    }

    [Fact]
    public void Marginal_when_battery_dips_below_limit()
    {
        var r = MissionSimulator.Run(Fixtures.Series(Sun, Earth), Lander, Fixtures.T0, EightHours, new MissionThresholds(60, 40));
        Assert.Equal(Verdict.Marginal, r.Verdict);
        Assert.Equal(1, r.HoursBelowThreshold, 9); // only the last hour (50 %) is below 60 %
        Assert.Equal("battery drops to 50% (below the 60% limit) on day 1", r.Reason);
    }

    [Fact]
    public void Marginal_when_comms_below_target()
    {
        var r = MissionSimulator.Run(Fixtures.Series(Sun, Earth), Lander, Fixtures.T0, EightHours, new MissionThresholds(20, 60));
        Assert.Equal(Verdict.Marginal, r.Verdict);
        Assert.Equal("Earth visible only 50% of the time (target 60%)", r.Reason);
    }

    [Fact]
    public void Landing_offset_and_mission_day()
    {
        var sun = Fixtures.Repeat([100], 72);
        var r = MissionSimulator.Run(Fixtures.Series(sun, sun.Select(_ => 1).ToArray()), Lander, Fixtures.T0.AddHours(24), 1, Thresholds);
        Assert.Equal(24, r.StartIndex);
        Assert.Equal(Fixtures.T0.AddHours(24), r.LandingUtc);
        Assert.Equal(1, r.MissionDay(23));
        Assert.Equal(2, r.MissionDay(24 - 1 + 1));
    }

    [Fact]
    public void Mission_outside_data_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MissionSimulator.Run(Fixtures.Series(Sun, Earth), Lander, Fixtures.T0, 1, Thresholds));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MissionSimulator.Run(Fixtures.Series(Sun, Earth), Lander, Fixtures.T0.AddDays(-1), EightHours, Thresholds));
    }

    [Fact]
    public void Unspecified_kind_is_treated_as_utc()
    {
        var local = DateTime.SpecifyKind(Fixtures.T0, DateTimeKind.Unspecified);
        Assert.Equal(0, Fixtures.Series(Sun, Earth).IndexOf(local));
    }

    private sealed class ToleranceComparer(double tol) : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) <= tol;
        public int GetHashCode(double v) => 0;
    }
}
