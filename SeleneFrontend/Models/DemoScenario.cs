namespace SeleneFrontend.Models;

/// <summary>
/// QUICK DEMO: a landing that works but visibly drains the battery, and its counter-example. Chosen 2026-11-15 with
/// MissionSimulator over 2027–2028 (9 regions × Nova-C / Blue Ghost × every day, 7-day missions): the only scenario with
/// one mid-mission darkness of 5–12 h, Earth contact ≥ 80 % and the lowest battery between 30 and 70 % without failing.
/// Nova-C's battery is too large for such a dip (its 7 h darkness at Haworth 2028-10-24 keeps 100 %).
/// <list type="bullet">
/// <item>Demo: Blue Ghost at Nobile Rim 1, landing 2028-12-26 — FEASIBLE, 11 h of darkness from day 3.9, lowest battery ≈ 41 %.</item>
/// <item>Counter-example: the same site and lander 4 weeks later (2029-01-23) — 33 h of darkness from day 5, battery empty → MISSION LOST.</item>
/// </list>
/// Values are checked by <c>DemoScenarioTests</c>. UI-free.
/// </summary>
public static class DemoScenario
{
    public const string SiteId = "nobile_rim_1";
    public const string LanderId = "blue_ghost";
    public const double MissionDays = 7;
    public static readonly DateTime LandingUtc = new(2028, 12, 26, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime CounterLandingUtc = new(2029, 1, 23, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Landing window of the demo (24 months, both dates inside), so "Show the best landing window" still optimises.</summary>
    public static readonly DateTime WindowStart = new(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime WindowEnd = new(2029, 1, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Button text on the demo's summary card (the counter-example is the same site and lander).</summary>
    public static string CounterText => $"The same site {(CounterLandingUtc - LandingUtc).TotalDays / 7:0} weeks later would fail →";
}
