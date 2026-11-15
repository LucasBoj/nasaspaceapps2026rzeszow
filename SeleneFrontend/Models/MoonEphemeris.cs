namespace SeleneFrontend.Models;

/// <summary>
/// One calendar year of <c>data/ephem/{year}.bin</c>: per hour the apparent direction of the Sun and the Earth seen
/// from the Moon's centre (MOON_ME_DE421, Skyfield, as the pipeline) as sub-solar / sub-Earth latitude, longitude
/// and distance. Stored here as Moon-fixed vectors in km, ready for the topocentric correction.
/// </summary>
public sealed class MoonEphemerisYear
{
    public DateTime Start { get; }
    public int Count { get; }
    public double[] Sun { get; }     // x, y, z (km) per hour
    public double[] Earth { get; }

    public MoonEphemerisYear(DateTime start, int count, double[] sun, double[] earth)
    {
        (Start, Count, Sun, Earth) = (TimeUtil.AsUtc(start), count, sun, earth);
    }

    /// <summary>Decode float32 little endian, 6 values per hour: sunLat, sunLon, sunDistKm, earthLat, earthLon, earthDistKm.</summary>
    public static MoonEphemerisYear FromBinary(DateTime start, byte[] data)
    {
        var n = data.Length / 24;
        var sun = new double[n * 3];
        var earth = new double[n * 3];
        for (var i = 0; i < n; i++)
        {
            float F(int k) => BitConverter.ToSingle(data, (i * 6 + k) * 4);
            Vec(F(0), F(1), F(2), sun, i);
            Vec(F(3), F(4), F(5), earth, i);
        }
        return new MoonEphemerisYear(start, n, sun, earth);
    }

    private static void Vec(double latDeg, double lonDeg, double distKm, double[] into, int i)
    {
        var lat = latDeg * Math.PI / 180;
        var lon = lonDeg * Math.PI / 180;
        into[3 * i] = distKm * Math.Cos(lat) * Math.Cos(lon);
        into[3 * i + 1] = distKm * Math.Cos(lat) * Math.Sin(lon);
        into[3 * i + 2] = distKm * Math.Sin(lat);
    }
}

/// <summary>
/// Builds an <see cref="IlluminationSeries"/> for any surface point from the horizon mask and the ephemeris, with
/// exactly the pipeline's formulas (pipeline/illumination.py): topocentric elevation/azimuth (observer on the lunar
/// sphere, i.e. with the Earth's parallax of up to 0.26°), horizon linearly interpolated between whole degrees,
/// <c>sunFraction = clamp((el − h + r) / 2r)</c> with r = 0.267°, Earth visible when its centre is 0.5° above the horizon.
/// UI-free (unit-tested against the pipeline's data).
/// </summary>
public static class CustomSiteMath
{
    public const double SunRadiusDeg = 0.267;
    public const double EarthMarginDeg = 0.5;
    private const double MoonRadiusKm = PolarDem.MoonRadiusM / 1000;

    public static IlluminationSeries NewSeries(string siteId, double lat, double lon, double[] horizon, DateTime start, int count) => new()
    {
        SiteId = siteId, Lat = lat, Lon = lon, ObserverHeightM = PolarDem.ObserverHeightM,
        Start = TimeUtil.AsUtc(start), StepHours = 1, Count = count,
        SunFraction = new int[count], SunElevCentiDeg = new int[count], SunAzCentiDeg = new int[count],
        EarthVisible = new int[count], EarthElevCentiDeg = new int[count], EarthAzCentiDeg = new int[count],
        HorizonDeg = horizon,
    };

    /// <summary>Fill samples [<paramref name="from"/>, <paramref name="to"/>) of <paramref name="s"/> from one ephemeris year.</summary>
    public static void Fill(IlluminationSeries s, MoonEphemerisYear eph, int ephOffset, int from, int to)
    {
        var lat = s.Lat * Math.PI / 180;
        var lon = s.Lon * Math.PI / 180;
        var (sl, cl, so, co) = (Math.Sin(lat), Math.Cos(lat), Math.Sin(lon), Math.Cos(lon));
        var up = (X: cl * co, Y: cl * so, Z: sl);
        var east = (X: -so, Y: co, Z: 0.0);
        var north = (X: -sl * co, Y: -sl * so, Z: cl);
        var obs = (X: MoonRadiusKm * up.X, Y: MoonRadiusKm * up.Y, Z: MoonRadiusKm * up.Z);
        for (var i = from; i < to; i++)
        {
            var j = ephOffset + (i - from);
            var (sunEl, sunAz) = ElAz(eph.Sun, j, obs, up, east, north);
            var (earthEl, earthAz) = ElAz(eph.Earth, j, obs, up, east, north);
            var hs = HorizonAt(s.HorizonDeg, sunAz);
            var he = HorizonAt(s.HorizonDeg, earthAz);
            s.SunFraction[i] = (int)Math.Round(Math.Clamp((sunEl - hs + SunRadiusDeg) / (2 * SunRadiusDeg), 0, 1) * 100, MidpointRounding.ToEven);
            s.SunElevCentiDeg[i] = (int)Math.Round(sunEl * 100, MidpointRounding.ToEven);
            s.SunAzCentiDeg[i] = (int)Math.Round(sunAz * 100, MidpointRounding.ToEven);
            s.EarthVisible[i] = earthEl > he + EarthMarginDeg ? 1 : 0;
            s.EarthElevCentiDeg[i] = (int)Math.Round(earthEl * 100, MidpointRounding.ToEven);
            s.EarthAzCentiDeg[i] = (int)Math.Round(earthAz * 100, MidpointRounding.ToEven);
        }
    }

    /// <summary>Topocentric elevation and azimuth (deg, azimuth clockwise from north) of a body vector seen from the observer.</summary>
    public static (double El, double Az) ElAz(double[] body, int j,
        (double X, double Y, double Z) obs, (double X, double Y, double Z) up,
        (double X, double Y, double Z) east, (double X, double Y, double Z) north)
    {
        var tx = body[3 * j] - obs.X;
        var ty = body[3 * j + 1] - obs.Y;
        var tz = body[3 * j + 2] - obs.Z;
        var norm = Math.Sqrt(tx * tx + ty * ty + tz * tz);
        var u = (tx * up.X + ty * up.Y + tz * up.Z) / norm;
        var e = tx * east.X + ty * east.Y + tz * east.Z;
        var n = tx * north.X + ty * north.Y + tz * north.Z;
        var el = Math.Asin(Math.Clamp(u, -1, 1)) * 180 / Math.PI;
        var az = (Math.Atan2(e, n) * 180 / Math.PI + 360) % 360;
        return (el, az);
    }

    /// <summary>Horizon elevation at an azimuth, linear between whole degrees (wraps at 360°) — pipeline horizon_at().</summary>
    public static double HorizonAt(double[] horizon, double azDeg)
    {
        var n = horizon.Length;
        var a = (azDeg % 360 + 360) % 360 * n / 360.0;
        var k = (int)Math.Floor(a);
        var f = a - k;
        return horizon[k % n] * (1 - f) + horizon[(k + 1) % n] * f;
    }
}
