using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>Lander power figures used by the simulation (effective values, what-if applied).</summary>
/// <param name="WeakestKind">Weakest provenance of the inputs: sourced / derived / assumption / whatif.</param>
public sealed record LanderInputs(double SolarArrayW, double BatteryWh, double DayLoadW, double SurvivalLoadW, string WeakestKind);

/// <summary>Constraints from section 04.</summary>
public sealed record MissionThresholds(double MinBatteryPct, double MinCommsPct);

public enum Verdict { Feasible, Marginal, NotFeasible }

public sealed record SimulationResult
{
    public DateTime LandingUtc { get; init; }
    public int StartIndex { get; init; }
    public double StepHours { get; init; }
    public int Steps { get; init; }

    // Hourly series (empty when the simulation ran in metrics-only mode).
    public int[] Sun { get; init; } = [];
    public int[] Earth { get; init; } = [];
    public double[] SocPct { get; init; } = [];

    public double MinSocPct { get; init; }
    public int MinSocStep { get; init; }
    public double HoursBelowThreshold { get; init; }
    public double CommsPct { get; init; }
    public double SunPct { get; init; }
    public double LongestDarkH { get; init; }
    public bool Survived { get; init; }
    public int? DepletedAtStep { get; init; }

    public Verdict Verdict { get; init; }
    public string Reason { get; init; } = "";
    public string BasedOn { get; init; } = "";

    public double DurationDays => Steps * StepHours / 24.0;
    public DateTime TimeAt(int step) => LandingUtc.AddHours(step * StepHours);
    public int MissionDay(int step) => (int)Math.Floor(step * StepHours / 24.0) + 1;
}

/// <summary>Hour-by-hour energy and comms simulation of a landed mission. Pure logic, no UI.</summary>
public static class MissionSimulator
{
    /// <summary>A step is "day" for loads when at least half of the solar disk is visible.</summary>
    public const int LitThresholdPct = 50;

    /// <summary>
    /// Simulate from <paramref name="landingUtc"/> for <paramref name="durationDays"/>.
    /// Each step: production = solarArrayW × sunFraction/100; load = dayLoadW when lit,
    /// otherwise survivalLoadW; the battery starts full and is clamped to [0, batteryWh].
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The mission does not fit in the data.</exception>
    public static SimulationResult Run(IlluminationSeries series, LanderInputs lander, DateTime landingUtc,
                                       double durationDays, MissionThresholds thresholds, bool recordSeries = true)
    {
        var start = series.IndexOf(landingUtc);
        var steps = (int)Math.Ceiling(durationDays * 24.0 / series.StepHours - 1e-9);
        if (start < 0 || steps <= 0 || start + steps > series.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(landingUtc),
                $"Mission {landingUtc:yyyy-MM-dd HH:mm}Z + {durationDays:0.##} d is outside the data " +
                $"({series.Start:yyyy-MM-dd} … {series.LastSample:yyyy-MM-dd}).");
        }

        var dt = series.StepHours;
        var sun = recordSeries ? new int[steps] : [];
        var earth = recordSeries ? new int[steps] : [];
        var socPct = recordSeries ? new double[steps] : [];

        double soc = lander.BatteryWh;
        double minSocPct = 100;
        int minSocStep = 0;
        int belowSteps = 0, earthSteps = 0, litSteps = 0;
        double darkRunH = 0, longestDarkH = 0, darkRunAtDepletion = 0;
        int? depletedAt = null;
        bool depletedInDarkness = false;

        for (var i = 0; i < steps; i++)
        {
            var sf = series.SunFraction[start + i];
            var lit = sf >= LitThresholdPct;
            var production = lander.SolarArrayW * sf / 100.0;
            var load = lit ? lander.DayLoadW : lander.SurvivalLoadW;

            soc += (production - load) * dt;
            if (soc <= 1e-9)
            {
                soc = 0;
                if (depletedAt is null && load > production)
                {
                    depletedAt = i;
                    depletedInDarkness = !lit;
                    darkRunAtDepletion = lit ? 0 : darkRunH + dt;
                }
            }
            soc = Math.Min(soc, lander.BatteryWh);

            var pct = lander.BatteryWh > 0 ? soc / lander.BatteryWh * 100.0 : 0;
            if (pct < minSocPct) { minSocPct = pct; minSocStep = i; }
            if (pct < thresholds.MinBatteryPct) belowSteps++;

            var e = series.EarthVisible[start + i];
            earthSteps += e;
            if (lit) { litSteps++; darkRunH = 0; }
            else { darkRunH += dt; longestDarkH = Math.Max(longestDarkH, darkRunH); }

            if (recordSeries) { sun[i] = sf; earth[i] = e; socPct[i] = pct; }
        }

        var commsPct = 100.0 * earthSteps / steps;
        var result = new SimulationResult
        {
            LandingUtc = TimeUtil.AsUtc(series.TimeAt(start)),
            StartIndex = start,
            StepHours = dt,
            Steps = steps,
            Sun = sun,
            Earth = earth,
            SocPct = socPct,
            MinSocPct = minSocPct,
            MinSocStep = minSocStep,
            HoursBelowThreshold = belowSteps * dt,
            CommsPct = commsPct,
            SunPct = 100.0 * litSteps / steps,
            LongestDarkH = longestDarkH,
            Survived = depletedAt is null,
            DepletedAtStep = depletedAt,
            BasedOn = lander.WeakestKind,
        };

        var (verdict, reason) = Judge(result, thresholds, depletedInDarkness, darkRunAtDepletion);
        return result with { Verdict = verdict, Reason = reason };
    }

    private static (Verdict, string) Judge(SimulationResult r, MissionThresholds t, bool depletedInDarkness, double darkRunAtDepletion)
    {
        if (r.DepletedAtStep is { } d)
        {
            var day = r.MissionDay(d);
            return depletedInDarkness
                ? (Verdict.NotFeasible, $"battery depleted after {darkRunAtDepletion:0} h of darkness on day {day}")
                : (Verdict.NotFeasible, $"battery depleted on day {day} — daytime load exceeds solar output");
        }
        if (r.MinSocPct < t.MinBatteryPct)
        {
            return (Verdict.Marginal,
                $"battery drops to {r.MinSocPct:0}% (below the {t.MinBatteryPct:0}% limit) on day {r.MissionDay(r.MinSocStep)}");
        }
        if (r.CommsPct < t.MinCommsPct)
        {
            return (Verdict.Marginal, $"Earth visible only {r.CommsPct:0}% of the time (target {t.MinCommsPct:0}%)");
        }
        return (Verdict.Feasible, $"battery never below {r.MinSocPct:0}%, Earth visible {r.CommsPct:0}% of the time");
    }
}
