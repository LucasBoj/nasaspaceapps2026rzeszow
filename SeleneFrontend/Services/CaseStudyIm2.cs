using SeleneFrontend.Models;

namespace SeleneFrontend.Services;

/// <summary>Minimum, median and maximum of a set of values.</summary>
public sealed record Spread(double Min, double Median, double Max)
{
    public static Spread Of(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return new(double.NaN, double.NaN, double.NaN);
        var mid = v.Length / 2;
        var median = v.Length % 2 == 1 ? v[mid] : (v[mid - 1] + v[mid]) / 2;
        return new(v[0], median, v[^1]);
    }
}

/// <summary>Results over a set of lander orientations (headings, or fall directions × headings).</summary>
/// <param name="PowerMin">Per-step panel power envelope (W) over the first <see cref="CaseStudyIm2.EnvelopeHours"/> hours.</param>
public sealed record OrientationCase(Spread WindowEnergyWh, Spread SurvivedHours, Spread MeanWindowPowerW,
                                     double[] PowerMin, double[] PowerMedian, double[] PowerMax, int Orientations);

/// <summary>Battery-only endurance with no sunlight at all (e.g. the floor of a dark crater).</summary>
/// <param name="SurvivalLoadHours">Hours on the survival load (the model's load when it is dark).</param>
/// <param name="DayLoadHours">Hours if the lander kept drawing its daytime load.</param>
public sealed record DarkCase(double SurvivalLoadHours, double DayLoadHours);

/// <summary>
/// IM-2 "Athena" retrospective (data from pipeline/case_study_im2.py). Pure logic, no UI.
/// Every comparison is made over orientations, never against one hand-picked heading: upright over all
/// headings, and "on its side" (panel normals rotated by 90° about a horizontal axis, so the lander's up
/// axis points to the fall azimuth β) over all fall directions × pre-fall headings. Which way Athena
/// faced or fell is not published.
/// </summary>
public static class CaseStudyIm2
{
    /// <summary>Hours of the per-step power envelope returned for the chart.</summary>
    public const int EnvelopeHours = 48;

    /// <summary>
    /// The panel layout of a lander tipped by <paramref name="tiltDeg"/> towards azimuth <paramref name="fallAzDeg"/>
    /// (clockwise from north), whose heading before the fall was <paramref name="headingDeg"/>. Facet normals are
    /// rotated with Rodrigues' formula about k = (−cos β, sin β, 0) (east, north, up), which takes the up axis to
    /// the horizontal direction β. The result uses heading 0 and keeps the upright layout's normalization K.
    /// </summary>
    public static PanelLayout Tipped(PanelLayout upright, double fallAzDeg, double headingDeg, double tiltDeg = 90)
    {
        var k = PanelModel.For(upright)?.Normalization
                ?? throw new ArgumentException("Only fixed panel layouts can be tipped.", nameof(upright));
        var b = Rad(fallAzDeg);
        var (kx, ky, kz) = (-Math.Cos(b), Math.Sin(b), 0.0);
        var th = Rad(tiltDeg);
        var (c, s) = (Math.Cos(th), Math.Sin(th));

        var facets = upright.Facets.Select(f =>
        {
            var e = Rad(f.NormalElevDeg);
            var a = Rad(headingDeg + f.AzOffsetDeg);
            var (vx, vy, vz) = (Math.Cos(e) * Math.Sin(a), Math.Cos(e) * Math.Cos(a), Math.Sin(e));
            // Rodrigues: v' = v cosθ + (k × v) sinθ + k (k·v)(1 − cosθ)
            var (cx, cy, cz) = (ky * vz - kz * vy, kz * vx - kx * vz, kx * vy - ky * vx);
            var dot = kx * vx + ky * vy + kz * vz;
            var x = vx * c + cx * s + kx * dot * (1 - c);
            var y = vy * c + cy * s + ky * dot * (1 - c);
            var z = vz * c + cz * s + kz * dot * (1 - c);
            var elev = Math.Asin(Math.Clamp(z, -1, 1)) * 180 / Math.PI;
            var az = Math.Abs(elev) > 89.999 ? 0 : (Math.Atan2(x, y) * 180 / Math.PI + 360) % 360;
            return new PanelFacet { Name = f.Name, AzOffsetDeg = az, NormalElevDeg = elev, Share = f.Share };
        }).ToList();

        return new PanelLayout
        {
            Mode = PanelLayout.FixedMode, Facets = facets, NormalizationOverride = k,
            Unit = upright.Unit, Kind = upright.Kind, Note = upright.Note, SourceUrl = upright.SourceUrl,
        };
    }

    /// <summary>Upright lander over every heading of the grid.</summary>
    public static OrientationCase Upright(IlluminationSeries series, LanderInputs lander, DateTime landingUtc,
                                          double durationDays, MissionThresholds thresholds, double windowHours,
                                          int headingSteps = 24)
    {
        RequireFixed(lander);
        var runs = Enumerable.Range(0, headingSteps).Select(j => lander with { HeadingDeg = 360.0 * j / headingSteps, IdealTracking = false });
        return Sweep(series, runs, landingUtc, durationDays, thresholds, windowHours);
    }

    /// <summary>The lander on its side: every fall azimuth × pre-fall heading of the grid.</summary>
    public static OrientationCase Tipped(IlluminationSeries series, LanderInputs lander, DateTime landingUtc,
                                         double durationDays, MissionThresholds thresholds, double windowHours,
                                         int fallSteps = 24, int headingSteps = 12)
    {
        var upright = RequireFixed(lander);
        var runs = from i in Enumerable.Range(0, fallSteps)
                   from j in Enumerable.Range(0, headingSteps)
                   select lander with
                   {
                       Panels = Tipped(upright, 360.0 * i / fallSteps, 360.0 * j / headingSteps),
                       HeadingDeg = 0, IdealTracking = false,
                   };
        return Sweep(series, runs, landingUtc, durationDays, thresholds, windowHours);
    }

    /// <summary>
    /// Battery-only endurance with no Sun at all: the same series with every sunFraction set to 0
    /// (Earth and geometry unchanged). Hours on the survival load and, as an upper bound on the drain,
    /// on the daytime load.
    /// </summary>
    public static DarkCase Dark(IlluminationSeries series, LanderInputs lander, DateTime landingUtc,
                                double durationDays, MissionThresholds thresholds)
    {
        var dark = WithoutSun(series);
        var onSurvival = MissionSimulator.Run(dark, lander, landingUtc, durationDays, thresholds, recordSeries: false);
        var onDay = MissionSimulator.Run(dark, lander with { SurvivalLoadW = lander.DayLoadW }, landingUtc, durationDays, thresholds, recordSeries: false);
        return new(Survived(onSurvival), Survived(onDay));
    }

    /// <summary>Solar energy (Wh) produced in the first <paramref name="windowHours"/> of the mission.</summary>
    public static double WindowEnergy(SimulationResult r, double windowHours)
    {
        var full = (int)Math.Floor(windowHours / r.StepHours + 1e-9);
        var rest = windowHours / r.StepHours - full;
        double wh = 0;
        for (var i = 0; i < Math.Min(full, r.PowerW.Length); i++) wh += r.PowerW[i] * r.StepHours;
        if (full < r.PowerW.Length) wh += r.PowerW[full] * r.StepHours * rest;
        return wh;
    }

    /// <summary>Hours until the battery ran empty (the whole mission if it survived).</summary>
    public static double Survived(SimulationResult r) => (r.DepletedAtStep ?? r.Steps) * r.StepHours;

    /// <summary>A copy of the series with no sunlight (sunFraction 0 everywhere).</summary>
    public static IlluminationSeries WithoutSun(IlluminationSeries s) => new()
    {
        SiteId = s.SiteId + "_dark", Lat = s.Lat, Lon = s.Lon, ObserverHeightM = s.ObserverHeightM,
        Start = s.Start, StepHours = s.StepHours, Count = s.Count,
        SunFraction = new int[s.Count],
        SunElevCentiDeg = s.SunElevCentiDeg, SunAzCentiDeg = s.SunAzCentiDeg,
        EarthVisible = s.EarthVisible, EarthElevCentiDeg = s.EarthElevCentiDeg, EarthAzCentiDeg = s.EarthAzCentiDeg,
        HorizonDeg = s.HorizonDeg,
    };

    private static OrientationCase Sweep(IlluminationSeries series, IEnumerable<LanderInputs> runs, DateTime landingUtc,
                                         double durationDays, MissionThresholds thresholds, double windowHours)
    {
        var energies = new List<double>();
        var survived = new List<double>();
        var envelope = new List<double[]>();
        foreach (var lander in runs)
        {
            var r = MissionSimulator.Run(series, lander, landingUtc, durationDays, thresholds);
            energies.Add(WindowEnergy(r, windowHours));
            survived.Add(Survived(r));
            envelope.Add(r.PowerW.Take(EnvelopeHours).ToArray());
        }

        var n = envelope.Min(p => p.Length);
        double[] Column(Func<Spread, double> pick) =>
            Enumerable.Range(0, n).Select(t => pick(Spread.Of(envelope.Select(p => p[t])))).ToArray();
        var energy = Spread.Of(energies);
        return new(energy, Spread.Of(survived), new(energy.Min / windowHours, energy.Median / windowHours, energy.Max / windowHours),
                   Column(x => x.Min), Column(x => x.Median), Column(x => x.Max), energies.Count);
    }

    private static PanelLayout RequireFixed(LanderInputs lander) =>
        lander.Panels is { IsFixed: true } p ? p : throw new ArgumentException("The lander needs a fixed panel layout.", nameof(lander));

    private static double Rad(double deg) => deg * Math.PI / 180;
}
