using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>The shown mission re-run at one point of the landing ellipse.</summary>
/// <param name="E">Offset east of the target (m).</param>
/// <param name="N">Offset north of the target (m).</param>
public sealed record EllipsePointResult(double E, double N, Verdict Verdict, double MinSocPct, double SunPct, double CommsPct, double? SlopeDeg);

/// <summary>How the verdict varies over the landing ellipse (a disc of <see cref="RadiusM"/> around the target).</summary>
public sealed record EllipseResult(double RadiusM, double SpacingM, IReadOnlyList<EllipsePointResult> Points)
{
    public int Count => Points.Count;
    public int Feasible => Points.Count(p => p.Verdict == Verdict.Feasible);
    public int Marginal => Points.Count(p => p.Verdict == Verdict.Marginal);
    public int NotFeasible => Points.Count(p => p.Verdict == Verdict.NotFeasible);
    /// <summary>Share of the landing area (equal-area grid cells) where the mission is FEASIBLE, 0..1.</summary>
    public double FeasibleShare => Count == 0 ? 0 : (double)Feasible / Count;
    public double MinSunPct => Points.Min(p => p.SunPct);
    public double MaxSunPct => Points.Max(p => p.SunPct);
    public double MinSocPct => Points.Min(p => p.MinSocPct);
    /// <summary>Steepest 120 m DEM slope in the ellipse (deg); null when no point has one.</summary>
    public double? MaxSlopeDeg
    {
        get
        {
            var slopes = Points.Where(p => p.SlopeDeg is not null).Select(p => p.SlopeDeg!.Value).ToList();
            return slopes.Count == 0 ? null : slopes.Max();
        }
    }
}

/// <summary>
/// Landing ellipse: the shown mission re-run at every grid point within the lander's landing accuracy.
/// The Sun and Earth directions are the site's (they are the same 480 m away); each point brings its own terrain horizon.
/// Pure logic, no UI (linked into SeleneFrontend.Tests). Same rules as <c>pipeline/illumination.py</c> and
/// <c>pipeline/ellipse.py → reconstruct</c>, validated there against a full recomputation (≤ 2 pp).
/// </summary>
public static class LandingEllipse
{
    public const double SunAngularRadiusDeg = 0.267;   // pipeline/illumination.py
    public const double EarthMarginDeg = 0.5;

    /// <summary>
    /// A point's series for samples [<paramref name="start"/>, <paramref name="start"/> + <paramref name="count"/>) of the site:
    /// Sun/Earth geometry copied, sunFraction and earthVisible rebuilt against the point's horizon. No horizon is attached
    /// (it is in the point's own frame), so terrain clearances are NaN — they only feed the window score, not the verdict.
    /// </summary>
    public static IlluminationSeries PointSeries(IlluminationSeries site, EllipsePoint point, int start, int count)
    {
        if (!site.HasSunGeometry || !site.HasEarthElevation || site.EarthAzCentiDeg.Length < site.Count)
            throw new InvalidOperationException($"{site.SiteId}: the ellipse needs Sun and Earth azimuth/elevation series.");
        if (start < 0 || count <= 0 || start + count > site.Count) throw new ArgumentOutOfRangeException(nameof(start));

        var sunEl = site.SunElevCentiDeg.AsSpan(start, count).ToArray();
        var sunAz = site.SunAzCentiDeg.AsSpan(start, count).ToArray();
        var earthEl = site.EarthElevCentiDeg.AsSpan(start, count).ToArray();
        var earthAz = site.EarthAzCentiDeg.AsSpan(start, count).ToArray();
        var sun = new int[count];
        var earth = new int[count];
        for (var i = 0; i < count; i++)
        {
            var hs = HorizonAt(point, sunAz[i] / 100.0);
            var f = Math.Clamp((sunEl[i] / 100.0 - hs + SunAngularRadiusDeg) / (2 * SunAngularRadiusDeg), 0, 1);
            sun[i] = (int)Math.Round(f * 100, MidpointRounding.ToEven);          // numpy rint
            earth[i] = earthEl[i] / 100.0 > HorizonAt(point, earthAz[i] / 100.0) + EarthMarginDeg ? 1 : 0;
        }
        return new IlluminationSeries
        {
            SiteId = $"{site.SiteId}@{point.E:0},{point.N:0}", Lat = point.Lat, Lon = point.Lon,
            ObserverHeightM = site.ObserverHeightM, Start = site.TimeAt(start), StepHours = site.StepHours, Count = count,
            SunFraction = sun, SunElevCentiDeg = sunEl, SunAzCentiDeg = sunAz,
            EarthVisible = earth, EarthElevCentiDeg = earthEl, EarthAzCentiDeg = earthAz,
        };
    }

    /// <summary>
    /// The point's horizon as a site-frame elevation limit at site-frame azimuth <paramref name="siteAzDeg"/>:
    /// h_p(az + shift) − tilt·cos(az − tiltAz), the point mask linearly interpolated in its own 1° grid.
    /// </summary>
    public static double HorizonAt(EllipsePoint p, double siteAzDeg)
    {
        var h = p.HorizonCentiDeg;
        var n = h.Length;
        var a = ((siteAzDeg + p.AzShiftDeg) % 360 + 360) % 360 * n / 360.0;
        var k = (int)Math.Floor(a);
        var f = a - k;
        var mask = (h[k % n] * (1 - f) + h[(k + 1) % n] * f) / 100.0;
        return mask - p.TiltDeg * Math.Cos((siteAzDeg - p.TiltAzDeg) * Math.PI / 180.0);
    }

    /// <summary>
    /// Re-run <paramref name="nominal"/> (same landing time, duration and heading) at every grid point within
    /// <paramref name="radiusM"/> of the target. The target itself keeps the nominal verdict (its reconstruction differs
    /// from the shipped series only by rounding). Returns null when the grid is empty or the series lacks geometry.
    /// </summary>
    public static EllipseResult? Analyze(IlluminationSeries site, LandingEllipseData ellipse, LanderInputs lander,
                                         SimulationResult nominal, MissionThresholds thresholds, double radiusM,
                                         int[]? horizonDeltaCentiDeg = null)
    {
        if (ellipse.Points.Count == 0 || !site.HasSunGeometry || !site.HasEarthElevation || site.EarthAzCentiDeg.Length < site.Count)
            return null;
        var start = site.IndexOf(nominal.LandingUtc);
        if (start < 0 || start + nominal.Steps > site.Count) return null;

        var radius = Math.Min(radiusM, ellipse.RadiusM);
        var pinned = lander with { HeadingDeg = nominal.HeadingDeg ?? lander.HeadingDeg };
        var results = new List<EllipsePointResult>();
        foreach (var p in ellipse.Points)
        {
            if (p.DistanceM > radius + 1e-6 || p.HorizonCentiDeg.Length == 0) continue;
            SimulationResult r;
            if (p.DistanceM < 1e-6) r = nominal;
            else
            {
                // Panels at another height than the grid's 2 m: shift the point's mask by the site's own change.
                var point = horizonDeltaCentiDeg is { } d && d.Length == p.HorizonCentiDeg.Length
                    ? new EllipsePoint { E = p.E, N = p.N, Lat = p.Lat, Lon = p.Lon, AzShiftDeg = p.AzShiftDeg, TiltDeg = p.TiltDeg,
                                         TiltAzDeg = p.TiltAzDeg, HorizonCentiDeg = p.HorizonCentiDeg.Zip(d, (a, b) => a + b).ToArray() }
                    : p;
                var series = PointSeries(site, point, start, nominal.Steps);
                r = MissionSimulator.Run(series, pinned, series.Start, nominal.DurationDays, thresholds, recordSeries: false);
            }
            results.Add(new EllipsePointResult(p.E, p.N, r.Verdict, r.MinSocPct, r.SunPct, r.CommsPct, p.SlopeDeg));
        }
        return results.Count == 0 ? null : new EllipseResult(radius, ellipse.SpacingM, results);
    }
}
