using System.Globalization;
using SeleneFrontend.Services;

namespace SeleneFrontend.Models;

/// <summary>
/// Plain-language wording of a simulation result for the SIMPLE view (no SoC, no "terrain margin").
/// UI-free so it is unit-tested together with the simulator.
/// </summary>
public static class VerdictText
{
    public static string Label(Verdict v) => v switch
    {
        Verdict.Feasible => "FEASIBLE",
        Verdict.Marginal => "MARGINAL",
        _ => "NOT FEASIBLE",
    };

    public static string Icon(Verdict v) => v switch
    {
        Verdict.Feasible => "✓",
        Verdict.Marginal => "⚠",
        _ => "✕",
    };

    public static string CssClass(Verdict v) => v switch
    {
        Verdict.Feasible => "v-feasible",
        Verdict.Marginal => "v-marginal",
        _ => "v-nogo",
    };

    public static string Date(DateTime utc) => utc.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// One sentence, e.g. "Land at Slater Plain on 21 Dec 2028 — sunlight all 7 days, battery never below 89%,
    /// constant contact with Earth."
    /// </summary>
    public static string Sentence(SimulationResult r, string siteName, double minBatteryPct, double minCommsPct)
    {
        var date = Date(r.LandingUtc);
        var days = Days(r.DurationDays);
        var sun = r.SunPct >= 99.5 ? $"sunlight all {days}" : $"sunlight {P(r.SunPct)}% of the time";
        var earth = r.CommsPct >= 99.5 ? "constant contact with Earth" : $"Earth contact {P(r.CommsPct)}% of the time";

        switch (r.Verdict)
        {
            case Verdict.Feasible:
                return $"Land at {siteName} on {date} — {sun}, battery never below {P(r.MinSocPct)}%, {earth}.";
            case Verdict.Marginal when r.MinSocPct < minBatteryPct:
                return $"Landing at {siteName} on {date} works, but the battery drops to {P(r.MinSocPct)}% " +
                       $"(your limit is {P(minBatteryPct)}%) on day {r.MissionDay(r.MinSocStep)}.";
            case Verdict.Marginal:
                return $"Landing at {siteName} on {date} works, but Earth is in view only {P(r.CommsPct)}% of the time " +
                       $"(your target is {P(minCommsPct)}%).";
            default:
                var day = r.DepletedAtStep is { } d ? r.MissionDay(d) : 1;
                var cause = r.Reason.Contains("darkness", StringComparison.Ordinal) ? "during a long stretch without sunlight"
                    : r.Reason.Contains("fixed panels", StringComparison.Ordinal) ? "because the Sun moves out of view of its fixed solar panels"
                    : "because the lander uses more power than its panels make";
                return $"Landing at {siteName} on {date} fails — the battery runs out on day {day} {cause}.";
        }
    }

    /// <summary>"7 days", "6.5 days", "1 day".</summary>
    public static string Days(double d) =>
        Math.Abs(d - 1) < 1e-9 ? "1 day" : $"{d.ToString(Math.Abs(d % 1) < 1e-9 ? "0" : "0.#", CultureInfo.InvariantCulture)} days";

    private static string P(double pct) => Math.Round(pct).ToString("0", CultureInfo.InvariantCulture);
}
