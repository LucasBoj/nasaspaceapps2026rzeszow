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

    /// <summary>
    /// Plain-language landing-ellipse line, e.g. "If it lands up to 400 m off target, it still works at 72% of the spots."
    /// </summary>
    public static string EllipseSentence(EllipseResult e, Verdict nominal)
    {
        var r = Math.Round(e.RadiusM).ToString("0", CultureInfo.InvariantCulture);
        var share = e.FeasibleShare * 100;
        if (nominal == Verdict.Feasible)
        {
            return e.Feasible == e.Count
                ? $"This lander can touch down up to {r} m off target — the mission works at every spot that close."
                : $"This lander can touch down up to {r} m off target — within that circle the mission works at {P(share)}% of the spots.";
        }
        return e.Feasible == 0
            ? $"Missing the target by up to {r} m does not help: no spot that close works either."
            : $"{P(share)}% of the spots within {r} m of the target would work.";
    }

    /// <summary>
    /// What the chance means: plain words in SIMPLE ("In 348 of 400 simulated landings…"), the interval and the verdict
    /// split in ANALYST. Lists only the uncertainties that were actually sampled.
    /// </summary>
    public static string UncertaintySentence(MonteCarloResult r, bool analyst)
    {
        var inv = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        if (r.LandingPoints > 1) parts.Add($"touching down anywhere within {Math.Round(r.Spec.RadiusM).ToString("0", inv)} m of the target");
        if (r.Spec.LoadDelta > 0) parts.Add($"power use {P(r.Spec.LoadDelta * 100)}% higher or lower");
        if (r.HeadingVaried && r.Spec.HeadingErrorDeg > 0) parts.Add($"the lander turned up to {P(r.Spec.HeadingErrorDeg)}° off its planned heading");
        var what = parts.Count == 0 ? "" : " — " + string.Join(", ", parts);
        if (!analyst)
            return $"The mission works in {r.Feasible} of {r.Samples} simulated landings{what}.";
        return $"Monte Carlo, {r.Samples} landings{what}: FEASIBLE {P(r.PFeasible * 100)}% (95% interval {P(r.CiLow * 100)}–{P(r.CiHigh * 100)}%), " +
               $"MARGINAL {P(r.PMarginal * 100)}%, NOT FEASIBLE {P(r.PNotFeasible * 100)}%.";
    }

    /// <summary>"7 days", "6.5 days", "1 day".</summary>
    public static string Days(double d) =>
        Math.Abs(d - 1) < 1e-9 ? "1 day" : $"{d.ToString(Math.Abs(d % 1) < 1e-9 ? "0" : "0.#", CultureInfo.InvariantCulture)} days";

    private static string P(double pct) => Math.Round(pct).ToString("0", CultureInfo.InvariantCulture);
}
