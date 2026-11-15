namespace SeleneFrontend.Models;

/// <summary>
/// <c>data/thermal/diviner.json</c>: LRO Diviner surface temperatures at the landing sites, written by
/// <c>pipeline/diviner_temperatures.py</c> from the Diviner Polar Cumulative Product (Williams et al. 2019).
/// Optional for the app: without it the heater model treats the ground as cold as the sky (conservative).
/// </summary>
public sealed class DivinerData
{
    public string Source { get; set; } = "";
    public string Dataset { get; set; } = "";
    public string Citation { get; set; } = "";
    public string Fetched { get; set; } = "";
    public double BinDeg { get; set; } = 3.75;
    public List<DivinerPoint> Points { get; set; } = [];

    public DivinerPoint? For(string siteId) => Points.FirstOrDefault(p => p.Id == siteId);
}

/// <summary>
/// The 240 m Diviner bin containing one point: average bolometric surface temperature (K) for each bin of subsolar
/// longitude (east; bin i covers i·binDeg … (i+1)·binDeg) in southern summer (subsolar latitude &lt; 0) and winter.
/// </summary>
public sealed class DivinerPoint
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double BinLat { get; set; }
    public double BinLon { get; set; }
    public double BinDistanceM { get; set; }
    public double[] SummerK { get; set; } = [];
    public double[] WinterK { get; set; } = [];
    public int SummerKFilled { get; set; }
    public int WinterKFilled { get; set; }
    public double[] SummerKMin3x3 { get; set; } = [];
    public double[] SummerKMax3x3 { get; set; } = [];
    public double[] WinterKMin3x3 { get; set; } = [];
    public double[] WinterKMax3x3 { get; set; } = [];
    public double MinK { get; set; }
    public double MaxK { get; set; }
    public double MeanK { get; set; }

    public bool IsValid => SummerK.Length > 0 && SummerK.Length == WinterK.Length;

    /// <summary>
    /// Surface temperature (K) for the Sun at subsolar point (<paramref name="subsolarLatDeg"/>, <paramref name="subsolarLonDeg"/>):
    /// the season's curve, linearly interpolated between bin centres around the subsolar-longitude circle.
    /// </summary>
    public double TempK(double subsolarLatDeg, double subsolarLonDeg)
    {
        var curve = subsolarLatDeg < 0 ? SummerK : WinterK;
        var n = curve.Length;
        var binDeg = 360.0 / n;
        var pos = ((subsolarLonDeg % 360 + 360) % 360) / binDeg - 0.5;   // bin centres at (i + 0.5)·binDeg
        var k = (int)Math.Floor(pos);
        var f = pos - k;
        var a = curve[((k % n) + n) % n];
        var b = curve[(((k + 1) % n) + n) % n];
        return a + (b - a) * f;
    }

    /// <summary>
    /// Subsolar point seen from a site: the point at angular distance 90° − elevation along the Sun's azimuth
    /// (clockwise from lunar north). Exact on a sphere; the Sun's parallax from the Moon is ~10⁻⁵ rad.
    /// </summary>
    public static (double LatDeg, double LonDeg) SubsolarPoint(double siteLatDeg, double siteLonDeg, double sunElevDeg, double sunAzDeg)
    {
        const double d2r = Math.PI / 180;
        var phi = siteLatDeg * d2r;
        var delta = (90 - sunElevDeg) * d2r;
        var az = sunAzDeg * d2r;
        var sinLat = Math.Sin(phi) * Math.Cos(delta) + Math.Cos(phi) * Math.Sin(delta) * Math.Cos(az);
        var lat = Math.Asin(Math.Clamp(sinLat, -1, 1));
        var dLon = Math.Atan2(Math.Sin(az) * Math.Sin(delta) * Math.Cos(phi), Math.Cos(delta) - Math.Sin(phi) * sinLat);
        var lon = (siteLonDeg + dLon / d2r) % 360;
        return (lat / d2r, lon < 0 ? lon + 360 : lon);
    }
}
