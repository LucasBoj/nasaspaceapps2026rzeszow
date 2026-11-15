using System.Globalization;
using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>The shown mission next to the same mission at the region's ice-first site.</summary>
/// <param name="SameDate">Ice-first site, same lander, landing date and duration (its own AUTO heading).</param>
/// <param name="BestDate">Ice-first site at its own best landing date in the same window (null when no day fits).</param>
public sealed record IceComparisonResult(string SunSiteName, SimulationResult Sun, PsrIceSite Ice, SimulationResult SameDate,
                                         SimulationResult? BestDate, PsrCandidate? SunCandidate, PsrCandidate? IceCandidate);

/// <summary>
/// Sun vs ice with full verdicts (stage 3 of the science layer). The ice-first site has the same precomputed data as the
/// 9 regions (yearly series, ellipse, heights, Diviner ground), so the same simulator gives a like-for-like verdict.
/// Pure logic, no UI (linked into SeleneFrontend.Tests).
/// </summary>
public static class IceComparison
{
    /// <summary>The same landing (date, duration, lander) at the ice-first site; the heading is its own AUTO choice.</summary>
    public static SimulationResult SameDate(IlluminationSeries ice, LanderInputs lander, SimulationResult nominal, MissionThresholds thresholds) =>
        MissionSimulator.Run(ice, lander, nominal.LandingUtc, nominal.DurationDays, thresholds, recordSeries: false);

    /// <summary>The ice-first site's own best landing in the window (best score; the longest survival when nothing is FEASIBLE).</summary>
    public static async Task<SimulationResult?> BestInWindowAsync(OptimizerSite ice, LanderInputs lander, DateTime firstDayUtc,
                                                                  DateTime lastDayUtc, double durationDays, MissionThresholds thresholds,
                                                                  CancellationToken ct = default)
    {
        var heat = await WindowOptimizer.RunAsync([ice], lander, firstDayUtc, lastDayUtc, durationDays, thresholds, ct: ct);
        if (heat.Days == 0) return null;
        var day = heat.BestDayFor(0);
        if (day < 0) day = heat.LongestSurvivalDayFor(0);
        return day < 0 ? null : MissionSimulator.Run(ice.Series, lander, heat.DayUtc(day), durationDays, thresholds, recordSeries: false);
    }

    /// <summary>
    /// Plain sentence: "Same lander and date at NR201, 0.3 km from the ice: NOT FEASIBLE (the battery runs out on day 4).
    /// Its best date in your window, 12 Mar 2028: FEASIBLE."
    /// </summary>
    public static string Sentence(IceComparisonResult r)
    {
        var km = r.IceCandidate is { } c ? $", {c.D1Km.ToString("0.0", CultureInfo.InvariantCulture)} km from the ice" : "";
        var same = $"Same lander and date at {r.Ice.CandidateId}{km}: {Outcome(r.SameDate)}.";
        if (r.BestDate is not { } best || best.LandingUtc == r.SameDate.LandingUtc) return same;
        return $"{same} Its best date in your window, {VerdictText.Date(best.LandingUtc)}: {Outcome(best)}.";
    }

    private static string Outcome(SimulationResult m) => m.Verdict switch
    {
        Verdict.Feasible => $"FEASIBLE (battery never below {Math.Round(m.MinSocPct).ToString("0", CultureInfo.InvariantCulture)}%)",
        Verdict.Marginal => $"MARGINAL (battery down to {Math.Round(m.MinSocPct).ToString("0", CultureInfo.InvariantCulture)}%)",
        _ => m.DepletedAtStep is { } d ? $"NOT FEASIBLE (the battery runs out on day {m.MissionDay(d)})" : "NOT FEASIBLE",
    };
}
