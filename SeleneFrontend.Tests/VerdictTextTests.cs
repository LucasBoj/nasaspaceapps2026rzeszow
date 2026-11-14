using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

/// <summary>Plain-language verdict sentence of the SIMPLE view (same hand-computed cases as MissionSimulatorTests).</summary>
public class VerdictTextTests
{
    private static readonly int[] Sun = [100, 100, 100, 0, 0, 0, 0, 0];
    private static readonly int[] Earth = [1, 1, 1, 1, 0, 0, 0, 0];
    private static readonly LanderInputs Lander = new(100, 200, 40, 20, ParamKind.Derived);
    private const double EightHours = 8.0 / 24.0;

    private static SimulationResult Run(LanderInputs lander, MissionThresholds t, int[]? sun = null, int[]? earth = null) =>
        MissionSimulator.Run(Fixtures.Series(sun ?? Sun, earth ?? Earth), lander, Fixtures.T0, EightHours, t);

    [Fact]
    public void Feasible_sentence_names_site_date_and_the_three_numbers()
    {
        var r = Run(Lander, new MissionThresholds(20, 40));
        Assert.Equal("Land at Slater Plain on 1 Jan 2027 — sunlight 38% of the time, battery never below 50%, Earth contact 50% of the time.",
            VerdictText.Sentence(r, "Slater Plain", 20, 40));
    }

    [Fact]
    public void Full_sun_and_constant_contact_read_naturally()
    {
        var r = MissionSimulator.Run(Fixtures.Series([100, 100, 100], [1, 1, 1]), Lander, Fixtures.T0, 3.0 / 24, new MissionThresholds(20, 40));
        var s = VerdictText.Sentence(r, "Slater Plain", 20, 40);
        Assert.Contains("sunlight all 0.1 days", s);
        Assert.Contains("constant contact with Earth", s);
        Assert.DoesNotContain("SoC", s);
    }

    [Fact]
    public void Marginal_battery_mentions_the_users_limit()
    {
        var r = Run(Lander, new MissionThresholds(60, 40));
        Assert.Equal(Verdict.Marginal, r.Verdict);
        Assert.Equal("Landing at Haworth on 1 Jan 2027 works, but the battery drops to 50% (your limit is 60%) on day 1.",
            VerdictText.Sentence(r, "Haworth", 60, 40));
    }

    [Fact]
    public void Marginal_comms_mentions_the_target()
    {
        var r = Run(Lander, new MissionThresholds(20, 60));
        Assert.Equal("Landing at Haworth on 1 Jan 2027 works, but Earth is in view only 50% of the time (your target is 60%).",
            VerdictText.Sentence(r, "Haworth", 20, 60));
    }

    [Fact]
    public void Not_feasible_in_darkness_says_when_the_battery_runs_out()
    {
        var r = Run(Lander with { BatteryWh = 50 }, new MissionThresholds(20, 40));
        Assert.Equal(Verdict.NotFeasible, r.Verdict);
        Assert.Equal("Landing at Haworth on 1 Jan 2027 fails — the battery runs out on day 1 during a long stretch without sunlight.",
            VerdictText.Sentence(r, "Haworth", 20, 40));
    }

    [Fact]
    public void Not_feasible_by_overload_explains_the_cause()
    {
        var r = MissionSimulator.Run(Fixtures.Series([100, 100, 100, 100, 100], [1, 1, 1, 1, 1]), Lander with { DayLoadW = 150 },
            Fixtures.T0, 5.0 / 24, new MissionThresholds(20, 40));
        Assert.EndsWith("because the lander uses more power than its panels make.", VerdictText.Sentence(r, "Haworth", 20, 40));
    }

    [Theory]
    [InlineData(7, "7 days")]
    [InlineData(6.5, "6.5 days")]
    [InlineData(1, "1 day")]
    public void Days_are_plain(double d, string expected) => Assert.Equal(expected, VerdictText.Days(d));

    [Fact]
    public void Labels_and_icons()
    {
        Assert.Equal("NOT FEASIBLE", VerdictText.Label(Verdict.NotFeasible));
        Assert.Equal("✓", VerdictText.Icon(Verdict.Feasible));
        Assert.Equal("v-marginal", VerdictText.CssClass(Verdict.Marginal));
    }
}
