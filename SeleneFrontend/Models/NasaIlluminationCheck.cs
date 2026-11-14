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
    public double? NasaSunPct60 { get; set; }        // NASA 60 m map, south of −85° only
    public double OursSunPct18y { get; set; }
    public double OursSunPct2027 { get; set; }
    public double DeltaSunPct { get; set; }          // ours 18.6 y − NASA 120 m, percentage points
    public double NasaEarthPct { get; set; }
    public double OursEarthAnyPct18y { get; set; }
}
