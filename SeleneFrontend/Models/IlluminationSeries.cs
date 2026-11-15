namespace SeleneFrontend.Models;

/// <summary>
/// One site's precomputed hourly series — mirrors <c>illumination/{siteId}/{year}.json</c>
/// (contract in CLAUDE.md; do not change field names). The repository joins consecutive
/// yearly files into one series covering the analysis window (<see cref="Concat"/>).
/// </summary>
public sealed class IlluminationSeries
{
    public string SiteId { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double ObserverHeightM { get; set; }
    public DateTime Start { get; set; }
    public double StepHours { get; set; } = 1;
    public int Count { get; set; }

    public int[] SunFraction { get; set; } = [];        // 0..100 % of the solar disk visible
    public int[] SunElevCentiDeg { get; set; } = [];
    public int[] SunAzCentiDeg { get; set; } = [];
    public int[] EarthVisible { get; set; } = [];       // 0 / 1
    public int[] EarthElevCentiDeg { get; set; } = [];
    public int[] EarthAzCentiDeg { get; set; } = [];   // added 2026-11-14; empty in older files
    public double[] HorizonDeg { get; set; } = [];

    /// <summary>Exclusive end of the data (time just after the last sample).</summary>
    public DateTime End => Start.AddHours(Count * StepHours);

    public DateTime LastSample => Start.AddHours((Count - 1) * StepHours);

    public DateTime TimeAt(int index) => Start.AddHours(index * StepHours);

    /// <summary>Sun elevation and azimuth are present for every sample (needed for fixed-panel incidence).</summary>
    public bool HasSunGeometry => Count > 0 && SunElevCentiDeg.Length >= Count && SunAzCentiDeg.Length >= Count;

    public bool HasEarthElevation => Count > 0 && EarthElevCentiDeg.Length >= Count;

    public bool HasHorizon => HorizonDeg.Length > 0;

    private double[]? _sunClearance, _earthClearance;

    /// <summary>
    /// Sun centre elevation above the terrain horizon at its azimuth, per sample (degrees; empty without
    /// horizon or Sun geometry). Same linear horizon interpolation as the pipeline.
    /// </summary>
    public double[] SunClearanceDeg() =>
        _sunClearance ??= HasSunGeometry && HasHorizon ? Clearance(SunElevCentiDeg, SunAzCentiDeg) : [];

    /// <summary>Earth centre elevation above the terrain horizon, per sample (degrees; empty when unavailable).</summary>
    public double[] EarthClearanceDeg() =>
        _earthClearance ??= HasHorizon && EarthElevCentiDeg.Length >= Count && EarthAzCentiDeg.Length >= Count && Count > 0
            ? Clearance(EarthElevCentiDeg, EarthAzCentiDeg) : [];

    private double[] Clearance(int[] elCenti, int[] azCenti)
    {
        var result = new double[Count];
        var n = HorizonDeg.Length;
        for (var i = 0; i < Count; i++)
        {
            var az = (azCenti[i] / 100.0 % 360 + 360) % 360 * n / 360.0;
            var k = (int)Math.Floor(az);
            var f = az - k;
            var h = HorizonDeg[k % n] * (1 - f) + HorizonDeg[(k + 1) % n] * f;
            result[i] = elCenti[i] / 100.0 - h;
        }
        return result;
    }

    /// <summary>
    /// LRO Diviner surface temperatures of the site's 240 m bin (set by the repository; not part of the yearly files).
    /// Null for custom points and when <c>data/thermal/diviner.json</c> is missing.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DivinerPoint? Ground { get; set; }

    private double[]? _groundK;

    /// <summary>
    /// Ground temperature (K) per sample: the Diviner curve at the subsolar point seen from the site (from the stored Sun
    /// elevation and azimuth). Empty without Diviner data or Sun geometry. Computed once per series.
    /// </summary>
    public double[] GroundTempK()
    {
        if (_groundK is not null && _groundK.Length == Count) return _groundK;
        if (Ground is not { IsValid: true } g || !HasSunGeometry) return [];
        var t = new double[Count];
        for (var i = 0; i < Count; i++)
        {
            var (lat, lon) = DivinerPoint.SubsolarPoint(Lat, Lon, SunElevCentiDeg[i] / 100.0, SunAzCentiDeg[i] / 100.0);
            t[i] = g.TempK(lat, lon);
        }
        return _groundK = t;
    }

    // Lazily computed trigonometry of the Sun direction (private fields: never serialised).
    private double[]? _cosEl, _sinEl, _cosAz, _sinAz;

    /// <summary>cos/sin of Sun elevation and azimuth per sample, computed once (the optimizer loop must not call trig).</summary>
    public (double[] CosEl, double[] SinEl, double[] CosAz, double[] SinAz) SunTrig()
    {
        if (_cosEl is null || _cosEl.Length != Count)
        {
            if (!HasSunGeometry) throw new InvalidOperationException($"{SiteId}: no Sun elevation/azimuth series.");
            var cosEl = new double[Count]; var sinEl = new double[Count];
            var cosAz = new double[Count]; var sinAz = new double[Count];
            for (var i = 0; i < Count; i++)
            {
                var el = SunElevCentiDeg[i] / 100.0 * Math.PI / 180.0;
                var az = SunAzCentiDeg[i] / 100.0 * Math.PI / 180.0;
                cosEl[i] = Math.Cos(el); sinEl[i] = Math.Sin(el);
                cosAz[i] = Math.Cos(az); sinAz[i] = Math.Sin(az);
            }
            (_cosEl, _sinEl, _cosAz, _sinAz) = (cosEl, sinEl, cosAz, sinAz);
        }
        return (_cosEl, _sinEl!, _cosAz!, _sinAz!);
    }

    /// <summary>Consecutive yearly parts of one site joined into a single series (same fields, same grid).</summary>
    public static IlluminationSeries Concat(IReadOnlyList<IlluminationSeries> parts)
    {
        if (parts.Count == 0) throw new ArgumentException("no parts", nameof(parts));
        if (parts.Count == 1) return parts[0];
        var first = parts[0];
        for (var i = 1; i < parts.Count; i++)
        {
            if (TimeUtil.AsUtc(parts[i].Start) != TimeUtil.AsUtc(parts[i - 1].End))
                throw new InvalidOperationException($"{first.SiteId}: yearly files are not consecutive ({parts[i - 1].End:u} vs {parts[i].Start:u}).");
        }
        static int[] Join(IEnumerable<int[]> arrays) => arrays.SelectMany(a => a).ToArray();
        return new IlluminationSeries
        {
            SiteId = first.SiteId, Lat = first.Lat, Lon = first.Lon, ObserverHeightM = first.ObserverHeightM,
            Start = first.Start, StepHours = first.StepHours, Count = parts.Sum(p => p.Count),
            SunFraction = Join(parts.Select(p => p.SunFraction)),
            SunElevCentiDeg = Join(parts.Select(p => p.SunElevCentiDeg)),
            SunAzCentiDeg = Join(parts.Select(p => p.SunAzCentiDeg)),
            EarthVisible = Join(parts.Select(p => p.EarthVisible)),
            EarthElevCentiDeg = Join(parts.Select(p => p.EarthElevCentiDeg)),
            EarthAzCentiDeg = parts.All(p => p.EarthAzCentiDeg.Length == p.Count) ? Join(parts.Select(p => p.EarthAzCentiDeg)) : [],
            HorizonDeg = first.HorizonDeg,
            Ground = first.Ground,
        };
    }

    /// <summary>Sample index containing <paramref name="utc"/>, or −1 if outside the data.</summary>
    public int IndexOf(DateTime utc)
    {
        var hours = (TimeUtil.AsUtc(utc) - TimeUtil.AsUtc(Start)).TotalHours;
        var i = (int)Math.Floor(hours / StepHours + 1e-9);
        return i >= 0 && i < Count ? i : -1;
    }
}

/// <summary><c>illumination/index.json</c> — only the fields the app needs.</summary>
public sealed class IlluminationIndex
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }           // last sample (inclusive)
    public double StepHours { get; set; } = 1;
    public List<int> Years { get; set; } = [];   // one file per site and year: {siteId}/{year}.json
    public List<IlluminationIndexSite> Sites { get; set; } = [];
}

public sealed class IlluminationIndexSite
{
    public string SiteId { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = [];   // "2027" → "nobile_rim_1/2027.json"
    public double PctSunlit { get; set; }
    public double PctComms { get; set; }
    public double LongestDarkHours { get; set; }
}

public static class TimeUtil
{
    /// <summary>
    /// Treat unspecified kinds as UTC. WebAssembly runs in the browser's time zone, so
    /// <see cref="DateTime.ToUniversalTime"/> on an unspecified value would shift it.
    /// </summary>
    public static DateTime AsUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(d, DateTimeKind.Utc),
        _ => d.ToUniversalTime(),
    };
}
