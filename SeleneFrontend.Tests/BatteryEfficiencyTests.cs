using System.Text.Json;
using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

public class BatteryEfficiencyTests
{
    // Same hand-computed case as MissionSimulatorTests: 3 h full sun, then 5 h dark.
    //   solar 100 W, battery 200 Wh, day load 40 W, survival load 20 W.
    private static readonly int[] Sun = [100, 100, 100, 0, 0, 0, 0, 0];
    private static readonly int[] Earth = [1, 1, 1, 1, 0, 0, 0, 0];
    private static readonly MissionThresholds Thresholds = new(MinBatteryPct: 20, MinCommsPct: 40);
    private const double EightHours = 8.0 / 24.0;

    private static LanderInputs Lander(double eta) => new(100, 200, 40, 20, ParamKind.Derived, BatteryRoundTripEff: eta);

    [Fact]
    public void Ideal_battery_matches_the_lossless_model()
    {
        var r = MissionSimulator.Run(Fixtures.Series(Sun, Earth), Lander(1.0), Fixtures.T0, EightHours, Thresholds);
        double[] expected = [100, 100, 100, 90, 80, 70, 60, 50];
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], r.SocPct[i], 9);
        Assert.Equal(0, r.BatteryLossWh, 9);
        Assert.Equal(1.0, r.BatteryRoundTripEff, 9);
    }

    [Fact]
    public void Discharge_draws_load_over_root_eta()
    {
        // η = 0.81 → √η = 0.9. Battery stays full in the sun (surplus shunted, no loss).
        // Each dark hour draws 20 / 0.9 = 22.222 Wh: 200 → 177.78 → … → 88.89 Wh (44.44%).
        // Loss = 5 × (22.222 − 20) = 11.111 Wh.
        var r = MissionSimulator.Run(Fixtures.Series(Sun, Earth), Lander(0.81), Fixtures.T0, EightHours, Thresholds);
        Assert.Equal(100, r.SocPct[2], 9);
        Assert.Equal(100 * (200 - 5 * 20 / 0.9) / 200, r.MinSocPct, 6);
        Assert.Equal(5 * (20 / 0.9 - 20), r.BatteryLossWh, 6);
        Assert.Equal(Verdict.Feasible, r.Verdict);
    }

    [Fact]
    public void Charge_stores_surplus_times_root_eta_up_to_full()
    {
        // 2 h dark, then 2 h sun. η = 0.81.
        //   dark: 200 − 2 × 22.222 = 155.556 Wh (loss 4.444)
        //   sun 1: surplus 60 Wh × 0.9 = 54 → would be 209.6, room is 44.444 → stores 44.444, takes 49.383 (loss 4.938)
        //   sun 2: full, surplus shunted, no loss
        var series = Fixtures.Series([0, 0, 100, 100], [1, 1, 1, 1]);
        var r = MissionSimulator.Run(series, Lander(0.81), Fixtures.T0, 4.0 / 24, Thresholds);
        var afterDark = 200 - 2 * 20 / 0.9;
        Assert.Equal(100 * afterDark / 200, r.SocPct[1], 6);
        Assert.Equal(100, r.SocPct[2], 6);
        Assert.Equal(2 * (20 / 0.9 - 20) + (200 - afterDark) * (1 / 0.9 - 1), r.BatteryLossWh, 6);
    }

    [Fact]
    public void Partial_charge_loses_one_minus_root_eta()
    {
        // Start full, 1 h dark (−22.222 Wh), then sunFraction 50 → 50 W − 40 W day load = 10 W surplus:
        // stores 10 × 0.9 = 9 Wh (loss 1 Wh): 177.778 + 9 = 186.778 Wh.
        var series = Fixtures.Series([0, 50], [1, 1]);
        var r = MissionSimulator.Run(series, Lander(0.81), Fixtures.T0, 2.0 / 24, Thresholds);
        Assert.Equal(100 * (200 - 20 / 0.9 + 9) / 200, r.SocPct[1], 6);
        Assert.Equal((20 / 0.9 - 20) + 1, r.BatteryLossWh, 6);
    }

    [Fact]
    public void Losses_lower_the_break_even_load()
    {
        var series = Fixtures.Series(Sun, Earth);
        double BreakEven(double eta)
        {
            var nominal = MissionSimulator.Run(series, Lander(eta), Fixtures.T0, EightHours, Thresholds);
            return SensitivityAnalysis.ForLoad(series, Lander(eta), nominal, Thresholds).FeasibleUpToFactor!.Value;
        }
        // Lossless: (200 − 100 f) / 200 ≥ 20% → f ≤ 1.6. With √η = 0.9: 200 − 100 f / 0.9 ≥ 40 → f ≤ 1.44.
        Assert.InRange(BreakEven(1.0), 1.595, 1.6 + 1e-9);
        Assert.InRange(BreakEven(0.81), 1.435, 1.44 + 1e-9);
    }

    [Fact]
    public void Every_lander_has_a_sourced_battery_efficiency()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SeleneFrontend", "wwwroot", "data", "landers.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var landers = JsonSerializer.Deserialize<List<LanderPreset>>(
            File.ReadAllText(Path.Combine(dir!.FullName, "SeleneFrontend", "wwwroot", "data", "landers.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        foreach (var l in landers)
        {
            Assert.NotNull(l.BatteryRoundTripEff);
            Assert.InRange(l.BatteryRoundTripEff!.Value, 0.5, 1.0);
            Assert.False(string.IsNullOrWhiteSpace(l.BatteryRoundTripEff.SourceUrl), $"{l.Id}: batteryRoundTripEff needs a sourceUrl");
        }
    }
}
