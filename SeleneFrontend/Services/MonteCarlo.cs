using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>Uncertainty ranges sampled by <see cref="MonteCarlo"/>.</summary>
/// <param name="RadiusM">Landing accuracy: touchdown uniform over the disc of this radius (landers.json → landingAccuracyM).</param>
/// <param name="LoadDelta">Loads (day and survival) × U(1 − δ, 1 + δ); same ±20 % as the robustness row (loads are partly ASSUMPTION).</param>
/// <param name="HeadingErrorDeg">Heading at touchdown = mission heading + U(−e, +e) (ASSUMPTION: no published figure).</param>
public sealed record UncertaintySpec(double RadiusM, double LoadDelta, double HeadingErrorDeg, int Samples, int Seed);

/// <summary>Outcome of the Monte Carlo run: share of simulated landings per verdict, with a 95 % interval for P(FEASIBLE).</summary>
public sealed record MonteCarloResult(UncertaintySpec Spec, int Samples, int Feasible, int Marginal, int NotFeasible,
                                      int LandingPoints, bool HeadingVaried, double CiLow, double CiHigh)
{
    public double PFeasible => Samples == 0 ? 0 : (double)Feasible / Samples;
    public double PMarginal => Samples == 0 ? 0 : (double)Marginal / Samples;
    public double PNotFeasible => Samples == 0 ? 0 : (double)NotFeasible / Samples;
}

/// <summary>
/// Monte Carlo over the three things a planner cannot fix exactly: where the lander touches down (landing ellipse),
/// how much power it really draws (loads) and the heading it ends up with (fixed panels only). Each sample re-runs the
/// shown mission (same landing time and duration) with one draw of each. Pure logic, no UI (linked into the tests).
/// Deterministic for a given seed, so the number on screen does not flicker between identical runs.
/// </summary>
public static class MonteCarlo
{
    public const int DefaultSamples = 400;
    public const int DefaultSeed = 20261115;
    public const double DefaultHeadingErrorDeg = 15;

    /// <param name="ellipse">Ellipse grid of the site (null → every sample lands on the target).</param>
    /// <param name="horizonDeltaCentiDeg">Shift of the point masks for panels at another height (as in <see cref="LandingEllipse.Analyze"/>).</param>
    public static MonteCarloResult? Run(IlluminationSeries site, LandingEllipseData? ellipse, LanderInputs lander,
                                        SimulationResult nominal, MissionThresholds thresholds, UncertaintySpec spec,
                                        int[]? horizonDeltaCentiDeg = null)
    {
        var start = site.IndexOf(nominal.LandingUtc);
        if (start < 0 || start + nominal.Steps > site.Count || spec.Samples <= 0) return null;

        // Landing points: the target plus every ellipse grid point within the radius. The grid is equal-area, so a uniform
        // pick among its points is a uniform touchdown over the disc. Their series are built once and reused.
        var points = new List<IlluminationSeries> { Slice(site, start, nominal.Steps) };
        if (ellipse is { Points.Count: > 0 } && site.HasSunGeometry && site.HasEarthElevation && site.EarthAzCentiDeg.Length >= site.Count)
        {
            var radius = Math.Min(spec.RadiusM, ellipse.RadiusM);
            foreach (var p in ellipse.Points)
            {
                if (p.DistanceM < 1e-6 || p.DistanceM > radius + 1e-6 || p.HorizonCentiDeg.Length == 0) continue;
                var point = horizonDeltaCentiDeg is { } d && d.Length == p.HorizonCentiDeg.Length
                    ? new EllipsePoint { E = p.E, N = p.N, Lat = p.Lat, Lon = p.Lon, AzShiftDeg = p.AzShiftDeg, TiltDeg = p.TiltDeg,
                                         TiltAzDeg = p.TiltAzDeg, HorizonCentiDeg = p.HorizonCentiDeg.Zip(d, (a, b) => a + b).ToArray() }
                    : p;
                points.Add(LandingEllipse.PointSeries(site, point, start, nominal.Steps));
            }
        }

        var heading = nominal.HeadingDeg;   // null with ideal tracking: nothing to vary
        var rng = new Random(spec.Seed);
        int feasible = 0, marginal = 0, notFeasible = 0;
        for (var i = 0; i < spec.Samples; i++)
        {
            var series = points[rng.Next(points.Count)];
            var load = 1 + spec.LoadDelta * (2 * rng.NextDouble() - 1);
            var headingErr = spec.HeadingErrorDeg * (2 * rng.NextDouble() - 1);   // drawn always, so draws stay aligned
            var inputs = SensitivityAnalysis.ScaleLoads(lander, load) with
            {
                HeadingDeg = heading is { } h ? ((h + headingErr) % 360 + 360) % 360 : lander.HeadingDeg,
            };
            var r = MissionSimulator.Run(series, inputs, series.Start, nominal.DurationDays, thresholds, recordSeries: false);
            switch (r.Verdict)
            {
                case Verdict.Feasible: feasible++; break;
                case Verdict.Marginal: marginal++; break;
                default: notFeasible++; break;
            }
        }

        var (lo, hi) = Wilson(feasible, spec.Samples);
        return new MonteCarloResult(spec, spec.Samples, feasible, marginal, notFeasible, points.Count, heading is not null, lo, hi);
    }

    /// <summary>95 % Wilson score interval for a binomial proportion.</summary>
    public static (double Low, double High) Wilson(int successes, int n, double z = 1.96)
    {
        if (n <= 0) return (0, 1);
        var p = (double)successes / n;
        var z2 = z * z;
        var centre = (p + z2 / (2 * n)) / (1 + z2 / n);
        var half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / (1 + z2 / n);
        return (Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    /// <summary>The site's own series cut to the mission (the target as a landing point; keeps its horizon).</summary>
    private static IlluminationSeries Slice(IlluminationSeries s, int start, int count)
    {
        static T[] Cut<T>(T[] a, int start, int count) => a.Length >= start + count ? a.AsSpan(start, count).ToArray() : [];
        return new IlluminationSeries
        {
            SiteId = s.SiteId, Lat = s.Lat, Lon = s.Lon, ObserverHeightM = s.ObserverHeightM,
            Start = s.TimeAt(start), StepHours = s.StepHours, Count = count,
            SunFraction = Cut(s.SunFraction, start, count), SunElevCentiDeg = Cut(s.SunElevCentiDeg, start, count),
            SunAzCentiDeg = Cut(s.SunAzCentiDeg, start, count), EarthVisible = Cut(s.EarthVisible, start, count),
            EarthElevCentiDeg = Cut(s.EarthElevCentiDeg, start, count), EarthAzCentiDeg = Cut(s.EarthAzCentiDeg, start, count),
            HorizonDeg = s.HorizonDeg,
            Ground = s.Ground,   // Diviner ground temperatures for the heater model
        };
    }
}
