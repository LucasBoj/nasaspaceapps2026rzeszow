namespace SeleneFrontend.Models;

/// <summary>
/// <c>data/validation/nasa_illumination.json</c>, written by <c>pipeline/compare_nasa_illumination.py</c>:
/// our sunlit time vs NASA's LOLA illumination maps (Mazarico et al. 2011) at the 9 sites.
/// Optional: the app works without it. Not part of the <c>illumination/*</c> data contract.
/// </summary>
public sealed class NasaIlluminationCheck
{
    public string Source { get; set; } = "";
    public string Citation { get; set; } = "";
    public string Url { get; set; } = "";
    public string Definition { get; set; } = "";
    public string NasaPeriod { get; set; } = "";
    public string OursPeriod18y { get; set; } = "";
    public string OursModel { get; set; } = "";
    public List<NasaSiteCheck> Sites { get; set; } = [];
}

public sealed class NasaSiteCheck
{
    public string SiteId { get; set; } = "";
    public double NasaSunPct { get; set; }           // NASA 120 m map (same DEM resolution as ours)
    /// <summary>[min, max] of the NASA 120 m map over the 3×3 pixels around the point (±120 m): landing-accuracy spread.</summary>
    public double[]? NasaSunRangePct { get; set; }
    public double? NasaSunPct60 { get; set; }        // NASA 60 m map, south of −85° only
    public double OursSunPct18y { get; set; }
    public double OursSunPct2027 { get; set; }
    public double DeltaSunPct { get; set; }          // ours 18.6 y − NASA 120 m, percentage points
    /// <summary>Our model at the same point with the observer at the surface (NASA's maps are for the surface); null in older files.</summary>
    public double? OursSunPct18y0m { get; set; }
    /// <summary>Ours at the surface − NASA 120 m (pp): the like-for-like difference; null in older files.</summary>
    public double? DeltaSunPct0m { get; set; }
    public double NasaEarthPct { get; set; }
    public double OursEarthAnyPct18y { get; set; }

    /// <summary>Lowest NASA sunlit share within ±1 pixel (120 m); NaN when the file has no range.</summary>
    public double NasaSunMinNearPct => NasaSunRangePct is [var lo, ..] ? lo : double.NaN;
    public double NasaSunMaxNearPct => NasaSunRangePct is [_, var hi, ..] ? hi : double.NaN;

    /// <summary>Sunlight lost (pp) on the NASA map if the lander lands one pixel (120 m) off in the worst direction.</summary>
    public double NearLossPp => NasaSunRangePct is [var lo, ..] ? Math.Max(0, NasaSunPct - lo) : double.NaN;
}
