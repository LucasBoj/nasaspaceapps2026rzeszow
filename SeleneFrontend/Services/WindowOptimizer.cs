using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>A site to evaluate: id, display name and its hourly series.</summary>
public sealed record OptimizerSite(string Id, string Name, IlluminationSeries Series);

/// <summary>Score grid "site × landing day" produced by <see cref="WindowOptimizer"/>.</summary>
public sealed class HeatmapResult
{
    public required IReadOnlyList<OptimizerSite> Sites { get; init; }
    public required DateTime FirstDayUtc { get; init; }
    public required int Days { get; init; }
    public required double[,] Scores { get; init; }      // [site, day], 0 = not feasible
    public required Verdict[,] Verdicts { get; init; }
    public int BestSite { get; init; } = -1;
    public int BestDay { get; init; } = -1;

    public DateTime DayUtc(int day) => FirstDayUtc.AddDays(day);

    /// <summary>Best landing day for one site (−1 if none is feasible).</summary>
    public int BestDayFor(int site)
    {
        int best = -1;
        for (var d = 0; d < Days; d++)
        {
            if (Scores[site, d] > 0 && (best < 0 || Scores[site, d] > Scores[site, best])) best = d;
        }
        return best;
    }
}

/// <summary>Evaluates every (site, landing day at 00:00 UTC) combination of a window.</summary>
public static class WindowOptimizer
{
    /// <summary>Days computed between UI yields.</summary>
    public const int YieldEveryDays = 20;

    /// <summary>
    /// Score 0–100.
    /// <code>
    ///   NOT FEASIBLE (battery depleted)          → 0
    ///   otherwise  50 × socMargin + 30 × comms + 20 × light
    ///     socMargin = minSoC% / 100              (0..1, how much battery is left at the worst hour)
    ///     comms     = Earth-visible hours / all  (0..1)
    ///     light     = lit hours (sunFraction ≥ 50) / all (0..1)
    /// </code>
    /// A surviving mission always scores at least 1, so it is distinguishable from "not feasible".
    /// </summary>
    public static double Score(SimulationResult r)
    {
        if (r.Verdict == Verdict.NotFeasible) return 0;
        var score = 50 * Math.Clamp(r.MinSocPct / 100, 0, 1)
                  + 30 * Math.Clamp(r.CommsPct / 100, 0, 1)
                  + 20 * Math.Clamp(r.SunPct / 100, 0, 1);
        return Math.Clamp(score, 1, 100);
    }

    /// <summary>
    /// Landing days in [<paramref name="firstDayUtc"/>, <paramref name="lastDayUtc"/>] whose whole mission fits in the data.
    /// </summary>
    public static (DateTime First, int Days) LandingDays(IlluminationSeries reference, DateTime firstDayUtc, DateTime lastDayUtc, double durationDays)
    {
        var first = TimeUtil.AsUtc(firstDayUtc).Date;
        var dataFirst = TimeUtil.AsUtc(reference.Start).Date;
        if (first < dataFirst) first = dataFirst;
        var latestFit = TimeUtil.AsUtc(reference.End).AddHours(-durationDays * 24).Date;
        var last = TimeUtil.AsUtc(lastDayUtc).Date;
        if (last > latestFit) last = latestFit;
        var days = (int)(last - first).TotalDays + 1;
        return (DateTime.SpecifyKind(first, DateTimeKind.Utc), Math.Max(0, days));
    }

    public static async Task<HeatmapResult> RunAsync(IReadOnlyList<OptimizerSite> sites, LanderInputs lander,
        DateTime firstDayUtc, DateTime lastDayUtc, double durationDays, MissionThresholds thresholds,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (sites.Count == 0) throw new ArgumentException("No sites to evaluate.", nameof(sites));
        var (first, days) = LandingDays(sites[0].Series, firstDayUtc, lastDayUtc, durationDays);

        var scores = new double[sites.Count, days];
        var verdicts = new Verdict[sites.Count, days];
        int bestSite = -1, bestDay = -1;
        double bestScore = 0;
        var total = Math.Max(1, sites.Count * days);
        var done = 0;

        for (var s = 0; s < sites.Count; s++)
        {
            for (var d = 0; d < days; d++)
            {
                ct.ThrowIfCancellationRequested();
                var r = MissionSimulator.Run(sites[s].Series, lander, first.AddDays(d), durationDays, thresholds, recordSeries: false);
                var score = Score(r);
                scores[s, d] = score;
                verdicts[s, d] = r.Verdict;
                // Ties go to the earliest landing day (the soonest opportunity), not to list order.
                if (score > bestScore || (score > 0 && score == bestScore && d < bestDay))
                {
                    bestScore = score; bestSite = s; bestDay = d;
                }

                done++;
                if (d % YieldEveryDays == YieldEveryDays - 1)
                {
                    progress?.Report((double)done / total);
                    await Task.Yield();
                }
            }
        }
        progress?.Report(1.0);

        return new HeatmapResult
        {
            Sites = sites, FirstDayUtc = first, Days = days,
            Scores = scores, Verdicts = verdicts, BestSite = bestSite, BestDay = bestDay,
        };
    }
}
