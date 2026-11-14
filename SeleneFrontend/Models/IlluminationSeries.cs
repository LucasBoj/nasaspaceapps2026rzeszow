namespace SeleneFrontend.Models;

/// <summary>
/// One site's precomputed hourly series — mirrors <c>illumination/{siteId}.json</c>
/// (contract in CLAUDE.md; do not change field names).
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
    public List<IlluminationIndexSite> Sites { get; set; } = [];
}

public sealed class IlluminationIndexSite
{
    public string SiteId { get; set; } = "";
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
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
