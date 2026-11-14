using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>Lander power figures used by the simulation (effective values, what-if applied).</summary>
/// <param name="WeakestKind">Weakest provenance of the inputs: sourced / derived / assumption / whatif.</param>
/// <param name="Panels">Array mounting; null or <c>ideal</c> = the array always faces the Sun.</param>
/// <param name="HeadingDeg">Lander heading at touchdown (clockwise from lunar north); null = AUTO (best for the mission).</param>
/// <param name="IdealTracking">What-if: ignore the layout and use ideal Sun tracking (upper bound).</param>
/// <param name="BatteryRoundTripEff">
/// Energy out / energy in of the battery, (0, 1]; 1 = ideal battery. Split evenly: charge and discharge each lose √η.
/// Loads are served from the array first (direct energy transfer), so only the energy that passes through the battery is lost.
/// </param>
public sealed record LanderInputs(double SolarArrayW, double BatteryWh, double DayLoadW, double SurvivalLoadW, string WeakestKind,
                                  PanelLayout? Panels = null, double? HeadingDeg = null, bool IdealTracking = false,
                                  double BatteryRoundTripEff = 1.0);

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
    public double[] PowerW { get; init; } = [];          // solar production per step
    public double[] SunElevDeg { get; init; } = [];      // empty when the file has no elevation series

    // ---- Panels and energy ----
    /// <summary>True when fixed body-mounted panels were simulated (Lambert incidence), false for ideal tracking.</summary>
    public bool FixedPanels { get; init; }
    /// <summary>Array output with the Sun at the layout's best direction (= solarArrayW); scale of <see cref="PowerW"/>.</summary>
    public double PeakPowerW { get; init; }
    /// <summary>Lander heading at touchdown used for the panels (null for ideal tracking).</summary>
    public double? HeadingDeg { get; init; }
    public bool HeadingAuto { get; init; }
    public double EnergyProducedWh { get; init; }        // gross solar output (before the battery is full)
    public double EnergyIdealWh { get; init; }           // the same with ideal Sun tracking
    public double EnergyLoadWh { get; init; }
    /// <summary>Produced / ideal (0..1): how much the incidence angle costs. NaN when the Sun never shows.</summary>
    public double PanelEfficiency { get; init; } = double.NaN;
    /// <summary>Battery round-trip efficiency used (1 = ideal battery).</summary>
    public double BatteryRoundTripEff { get; init; } = 1.0;
    /// <summary>Energy lost in the battery (charge + discharge), Wh.</summary>
    public double BatteryLossWh { get; init; }

    // ---- Sky geometry (NaN when the data file lacks the series) ----
    public double SunElevMinDeg { get; init; } = double.NaN;
    public double SunElevMaxDeg { get; init; } = double.NaN;
    public double SunElevMeanDeg { get; init; } = double.NaN;
    public double EarthElevMinDeg { get; init; } = double.NaN;
    public double EarthElevMaxDeg { get; init; } = double.NaN;
    public double LongestLitH { get; init; }
    public double LongestCommsGapH { get; init; }
    /// <summary>Lowest Sun-centre height above the terrain horizon during the mission (°; NaN without horizon data).</summary>
    public double SunClearanceMinDeg { get; init; } = double.NaN;
    /// <summary>Lowest Earth-centre height above the terrain horizon during the mission (°; NaN without horizon data).</summary>
    public double EarthClearanceMinDeg { get; init; } = double.NaN;

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
    /// Each step: production = solarArrayW × sunFraction/100 × incidence factor (1 for ideal tracking,
    /// Lambert cosine on fixed panels — see <see cref="SolarGeometry"/>); load = dayLoadW when lit,
    /// otherwise survivalLoadW; the load is served from the array first, a surplus charges the battery at √η,
    /// a deficit is drawn from it at 1/√η (η = round-trip efficiency); the battery starts full and is clamped to [0, batteryWh].
    /// Fixed panels fall back to ideal tracking when the data file has no Sun elevation/azimuth series.
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

        // ---- Panels: fixed facets with Lambert incidence, or ideal tracking ----
        var model = lander.IdealTracking || !series.HasSunGeometry ? null : PanelModel.For(lander.Panels);
        double? heading = null;
        Span<double> cosPhi = stackalloc double[model?.FacetCount ?? 0];
        Span<double> sinPhi = stackalloc double[model?.FacetCount ?? 0];
        double[] cosEl = [], sinEl = [], cosAz = [], sinAz = [];
        double invK = 1;
        if (model is not null)
        {
            heading = lander.HeadingDeg is { } h ? ((h % 360) + 360) % 360 : SolarGeometry.BestHeading(series, start, steps, model);
            model.FacetAzimuths(heading.Value, cosPhi, sinPhi);
            (cosEl, sinEl, cosAz, sinAz) = series.SunTrig();
            invK = 1 / model.Normalization;
        }
        var power = recordSeries ? new double[steps] : [];
        var hasElev = series.HasSunGeometry;
        var sunElev = recordSeries && hasElev ? new double[steps] : [];

        double soc = lander.BatteryWh;
        double minSocPct = 100;
        int minSocStep = 0;
        int belowSteps = 0, earthSteps = 0, litSteps = 0;
        double darkRunH = 0, longestDarkH = 0, darkRunAtDepletion = 0;
        double litRunH = 0, longestLitH = 0, gapRunH = 0, longestGapH = 0;
        double energyProduced = 0, energyIdeal = 0, energyLoad = 0;
        double elMin = double.PositiveInfinity, elMax = double.NegativeInfinity, elSum = 0;
        int? depletedAt = null;
        bool depletedInDarkness = false;
        var eta = lander.BatteryRoundTripEff is > 0 and <= 1 ? lander.BatteryRoundTripEff : 1.0;
        var etaHalf = Math.Sqrt(eta);                      // charge efficiency = discharge efficiency
        double batteryLoss = 0;
        double factorAtDepletion = 1;

        for (var i = 0; i < steps; i++)
        {
            var t = start + i;
            var sf = series.SunFraction[t];
            var lit = sf >= LitThresholdPct;
            var ideal = lander.SolarArrayW * sf / 100.0;
            var production = model is null || sf <= 0
                ? ideal
                : ideal * model.Raw(cosEl[t], sinEl[t], cosAz[t], sinAz[t], cosPhi, sinPhi) * invK;
            var load = lit ? lander.DayLoadW : lander.SurvivalLoadW;
            energyProduced += production * dt;
            energyIdeal += ideal * dt;
            energyLoad += load * dt;
            if (hasElev)
            {
                var el = series.SunElevCentiDeg[t] / 100.0;
                elMin = Math.Min(elMin, el); elMax = Math.Max(elMax, el); elSum += el;
                if (recordSeries) sunElev[i] = el;
            }
            if (recordSeries) power[i] = production;

            var net = (production - load) * dt;              // Wh at the bus
            if (net >= 0)
            {
                var stored = Math.Min(net * etaHalf, Math.Max(0, lander.BatteryWh - soc));   // surplus beyond full is shunted
                batteryLoss += stored / etaHalf - stored;
                soc += stored;
            }
            else
            {
                var drawn = -net / etaHalf;                   // chemical energy needed for the deficit
                batteryLoss += Math.Min(drawn, Math.Max(0, soc)) * (1 - etaHalf);
                soc -= drawn;
            }
            if (soc <= 1e-9)
            {
                soc = 0;
                if (depletedAt is null && load > production)
                {
                    depletedAt = i;
                    depletedInDarkness = !lit;
                    factorAtDepletion = ideal > 0 ? production / ideal : 1;
                    darkRunAtDepletion = lit ? 0 : darkRunH + dt;
                }
            }
            soc = Math.Min(soc, lander.BatteryWh);

            var pct = lander.BatteryWh > 0 ? soc / lander.BatteryWh * 100.0 : 0;
            if (pct < minSocPct) { minSocPct = pct; minSocStep = i; }
            if (pct < thresholds.MinBatteryPct) belowSteps++;

            var e = series.EarthVisible[start + i];
            earthSteps += e;
            if (lit) { litSteps++; darkRunH = 0; litRunH += dt; longestLitH = Math.Max(longestLitH, litRunH); }
            else { darkRunH += dt; longestDarkH = Math.Max(longestDarkH, darkRunH); litRunH = 0; }
            if (e == 1) gapRunH = 0;
            else { gapRunH += dt; longestGapH = Math.Max(longestGapH, gapRunH); }

            if (recordSeries) { sun[i] = sf; earth[i] = e; socPct[i] = pct; }
        }

        static double MinOver(double[] values, int from, int count)
        {
            if (values.Length < from + count) return double.NaN;
            var m = double.PositiveInfinity;
            for (var t = from; t < from + count; t++) m = Math.Min(m, values[t]);
            return m;
        }
        var sunClearanceMin = MinOver(series.SunClearanceDeg(), start, steps);
        var earthClearanceMin = MinOver(series.EarthClearanceDeg(), start, steps);

        double earthMin = double.NaN, earthMax = double.NaN;
        if (series.HasEarthElevation)
        {
            earthMin = double.PositiveInfinity; earthMax = double.NegativeInfinity;
            for (var t = start; t < start + steps; t++)
            {
                var el = series.EarthElevCentiDeg[t] / 100.0;
                earthMin = Math.Min(earthMin, el); earthMax = Math.Max(earthMax, el);
            }
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
            PowerW = power,
            SunElevDeg = sunElev,
            FixedPanels = model is not null,
            PeakPowerW = lander.SolarArrayW,
            HeadingDeg = heading,
            HeadingAuto = model is not null && lander.HeadingDeg is null,
            EnergyProducedWh = energyProduced,
            EnergyIdealWh = energyIdeal,
            EnergyLoadWh = energyLoad,
            PanelEfficiency = energyIdeal > 0 ? energyProduced / energyIdeal : double.NaN,
            BatteryRoundTripEff = eta,
            BatteryLossWh = batteryLoss,
            SunElevMinDeg = hasElev ? elMin : double.NaN,
            SunElevMaxDeg = hasElev ? elMax : double.NaN,
            SunElevMeanDeg = hasElev ? elSum / steps : double.NaN,
            EarthElevMinDeg = earthMin,
            EarthElevMaxDeg = earthMax,
            LongestLitH = longestLitH,
            LongestCommsGapH = longestGapH,
            SunClearanceMinDeg = sunClearanceMin,
            EarthClearanceMinDeg = earthClearanceMin,
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

        var (verdict, reason) = Judge(result, thresholds, depletedInDarkness, darkRunAtDepletion, factorAtDepletion);
        return result with { Verdict = verdict, Reason = reason };
    }

    /// <summary>
    /// How long (days, one-step resolution) the battery lasts from <paramref name="landingUtc"/> if the stay is extended:
    /// one simulation up to <paramref name="capDays"/> (or the end of the data) with the lander's heading.
    /// Pass the shown mission's heading in <see cref="LanderInputs.HeadingDeg"/> so the answer matches that mission;
    /// with a null heading the AUTO heading is chosen for the whole capped stay.
    /// </summary>
    public static double MaxSurvivableDays(IlluminationSeries series, LanderInputs lander, DateTime landingUtc,
                                           MissionThresholds thresholds, double capDays = 30)
    {
        var start = series.IndexOf(landingUtc);
        if (start < 0) return 0;
        var dt = series.StepHours;
        var capSteps = Math.Min((int)Math.Floor(capDays * 24 / dt + 1e-9), series.Count - start);
        if (capSteps <= 0) return 0;
        var r = Run(series, lander, series.TimeAt(start), capSteps * dt / 24.0, thresholds, recordSeries: false);
        return (r.DepletedAtStep ?? capSteps) * dt / 24.0;
    }

    /// <summary>Below this share of the ideal output, a daytime depletion is blamed on the panel geometry.</summary>
    public const double GeometryBlameFactor = 0.8;

    private static (Verdict, string) Judge(SimulationResult r, MissionThresholds t, bool depletedInDarkness,
                                           double darkRunAtDepletion, double factorAtDepletion)
    {
        if (r.DepletedAtStep is { } d)
        {
            var day = r.MissionDay(d);
            if (depletedInDarkness)
                return (Verdict.NotFeasible, $"battery depleted after {darkRunAtDepletion:0} h of darkness on day {day}");
            return r.FixedPanels && factorAtDepletion < GeometryBlameFactor
                ? (Verdict.NotFeasible,
                   $"battery depleted on day {day} — the Sun has moved out of the fixed panels' view (output {factorAtDepletion * 100:0}% of ideal)")
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
