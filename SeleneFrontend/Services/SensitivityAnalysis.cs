using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>
/// Does the verdict survive a ±<see cref="DeltaFraction"/> error in the power loads?
/// Day and survival loads are scaled together; panels, battery and heading stay as in the shown mission.
/// </summary>
/// <param name="FeasibleUpToFactor">
/// Largest load factor (1 = nominal) that is still FEASIBLE, to ~0.5% resolution;
/// null when the mission is not FEASIBLE even with zero load (e.g. too little Earth contact);
/// equal to <paramref name="MaxFactor"/> when it is FEASIBLE across the whole searched range.
/// </param>
public sealed record LoadSensitivity(
    double DeltaFraction,
    Verdict Low, Verdict Nominal, Verdict High,
    double MinSocLowPct, double MinSocNominalPct, double MinSocHighPct,
    double? FeasibleUpToFactor, double MaxFactor)
{
    /// <summary>The verdict is the same at −Δ, nominal and +Δ load.</summary>
    public bool Robust => Low == Nominal && High == Nominal;
}

/// <summary>Verdict sensitivity to uncertain inputs. Pure logic, no UI (linked into SeleneFrontend.Tests).</summary>
public static class SensitivityAnalysis
{
    /// <summary>Default load uncertainty: CLPS day loads are ASSUMPTION (70–75% of peak power).</summary>
    public const double DefaultLoadDelta = 0.20;

    /// <summary>Upper end of the break-even search (load × 3).</summary>
    public const double MaxLoadFactor = 3.0;

    public static LanderInputs ScaleLoads(LanderInputs lander, double factor) =>
        lander with { DayLoadW = lander.DayLoadW * factor, SurvivalLoadW = lander.SurvivalLoadW * factor };

    /// <summary>
    /// Re-run the mission of <paramref name="nominal"/> with loads × (1 ± <paramref name="delta"/>) and find the
    /// break-even load. The heading is pinned to the nominal mission's, so only the load changes
    /// (the AUTO heading maximises solar energy and does not depend on the load anyway).
    /// The verdict is monotone in the load (more load → lower or equal state of charge, comms unchanged), so bisection is exact.
    /// </summary>
    public static LoadSensitivity ForLoad(IlluminationSeries series, LanderInputs lander, SimulationResult nominal,
                                          MissionThresholds thresholds, double delta = DefaultLoadDelta)
    {
        var pinned = lander with { HeadingDeg = nominal.HeadingDeg ?? lander.HeadingDeg };
        var days = nominal.DurationDays;
        SimulationResult At(double f) =>
            MissionSimulator.Run(series, ScaleLoads(pinned, f), nominal.LandingUtc, days, thresholds, recordSeries: false);

        var low = At(1 - delta);
        var high = At(1 + delta);

        double? upTo;
        if (At(MaxLoadFactor).Verdict == Verdict.Feasible) upTo = MaxLoadFactor;
        else if (At(0).Verdict != Verdict.Feasible) upTo = null;
        else
        {
            double lo = 0, hi = MaxLoadFactor;           // Feasible at lo, not at hi
            while (hi - lo > 0.005)
            {
                var mid = (lo + hi) / 2;
                if (At(mid).Verdict == Verdict.Feasible) lo = mid; else hi = mid;
            }
            upTo = lo;
        }

        return new LoadSensitivity(delta, low.Verdict, nominal.Verdict, high.Verdict,
            low.MinSocPct, nominal.MinSocPct, high.MinSocPct, upTo, MaxLoadFactor);
    }
}
