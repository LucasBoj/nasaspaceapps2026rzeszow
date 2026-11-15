using System.Text.Json;
using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

/// <summary>QUICK DEMO must show a real decision: a working mission whose battery visibly drops, and a counter-example that fails.</summary>
public class DemoScenarioTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Data()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d!.FullName, "SeleneFrontend", "wwwroot", "data", "landers.json"))) d = d.Parent;
        return Path.Combine(d.FullName, "SeleneFrontend", "wwwroot", "data");
    }

    private static SimulationResult Run(DateTime landingUtc)
    {
        var lander = JsonSerializer.Deserialize<List<LanderPreset>>(File.ReadAllText(Path.Combine(Data(), "landers.json")), Web)!
            .Single(l => l.Id == DemoScenario.LanderId);
        var series = IlluminationSeries.Concat(new[] { 2028, 2029 }.Select(y => JsonSerializer.Deserialize<IlluminationSeries>(
            File.ReadAllText(Path.Combine(Data(), "illumination", DemoScenario.SiteId, $"{y}.json")), Web)!).ToList());
        var inputs = new LanderInputs(lander.SolarArrayW!.Value, lander.BatteryWh!.Value, lander.DayLoadW!.Value, lander.SurvivalLoadW!.Value,
            ParamKind.Assumption, lander.Panels, null, false, lander.BatteryRoundTripEff?.Value ?? 1.0);
        // App defaults: minimum battery 20 %, minimum Earth contact 50 %.
        return MissionSimulator.Run(series, inputs, landingUtc, DemoScenario.MissionDays, new MissionThresholds(20, 50));
    }

    private static List<(int Start, int Length)> DarkRuns(SimulationResult m)
    {
        var runs = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i <= m.Sun.Length; i++)
        {
            var dark = i < m.Sun.Length && m.Sun[i] < 50;
            if (dark && start < 0) start = i;
            if (!dark && start >= 0) { runs.Add((start, i - start)); start = -1; }
        }
        return runs;
    }

    [Fact]
    public void Quick_demo_works_but_the_battery_visibly_drops_in_one_mid_mission_darkness()
    {
        var m = Run(DemoScenario.LandingUtc);
        Assert.NotEqual(Verdict.NotFeasible, m.Verdict);
        Assert.InRange(m.MinSocPct, 30, 70);
        Assert.True(m.CommsPct >= 80, $"Earth contact {m.CommsPct:0.0} %");
        var dark = Assert.Single(DarkRuns(m));
        Assert.InRange(dark.Length * m.StepHours, 5, 12);
        Assert.InRange(dark.Start * m.StepHours / 24, 1, 6);   // in the middle of the mission, not at its ends
    }

    [Fact]
    public void Counter_example_at_the_same_site_four_weeks_later_ends_in_mission_lost()
    {
        var m = Run(DemoScenario.CounterLandingUtc);
        Assert.Equal(Verdict.NotFeasible, m.Verdict);
        Assert.NotNull(m.DepletedAtStep);
        Assert.Contains(DarkRuns(m), r => r.Length * m.StepHours > 16);
        Assert.StartsWith("The same site 4 weeks later", DemoScenario.CounterText);
    }

    [Fact]
    public void Both_dates_fit_the_demo_window()
    {
        Assert.InRange(DemoScenario.LandingUtc, DemoScenario.WindowStart, DemoScenario.WindowEnd);
        Assert.InRange(DemoScenario.CounterLandingUtc, DemoScenario.WindowStart, DemoScenario.WindowEnd);
        Assert.True(DemoScenario.WindowEnd <= DemoScenario.WindowStart.AddMonths(24));
    }
}
