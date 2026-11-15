namespace SeleneFrontend.Models;

/// <summary>
/// LOLA heights for 80–90°S, 240 m pixels, polar stereographic (<c>+proj=stere +lat_0=-90 +lat_ts=-90 +R=1737400</c>),
/// from <c>data/dem/south80_240m.{json,bin}</c> (pipeline/export_browser_data.py). Same sampling and horizon
/// method as pipeline/horizon.py: rays on the lunar sphere, bilinear heights, step = pixel size,
/// <c>elev = atan2(dh − d²/2R, d)</c>, observer 2 m above the terrain, rays to 150 km.
/// UI-free (unit-tested).
/// </summary>
public sealed class PolarDem
{
    public const double MoonRadiusM = 1_737_400.0;
    public const double ObserverHeightM = 2.0;
    public const double MaxDistanceM = 150_000.0;
    public const int Azimuths = 360;
    public const double CoverageLatDeg = -80.0;   // custom analysis covers 80–90°S

    public int Width { get; }
    public int Height { get; }
    public double PixelM { get; }
    public double OriginX { get; }   // x of the left edge of column 0
    public double OriginY { get; }   // y of the top edge of row 0 (north up)
    private readonly float[] _h;     // metres, NaN = no data

    public PolarDem(int width, int height, double pixelM, double originX, double originY, float[] heights)
    {
        if (heights.Length != width * height) throw new ArgumentException("height grid does not match its size");
        (Width, Height, PixelM, OriginX, OriginY, _h) = (width, height, pixelM, originX, originY, heights);
    }

    /// <summary>Decode the shipped Int16 file: little endian, row-delta encoded, −32768 = no data.</summary>
    public static PolarDem FromBinary(DemMeta meta, byte[] data)
    {
        var n = meta.Width * meta.Height;
        if (data.Length != n * 2) throw new InvalidDataException($"DEM file has {data.Length} bytes, expected {n * 2}");
        var h = new float[n];
        for (var r = 0; r < meta.Height; r++)
        {
            short acc = 0;
            for (var c = 0; c < meta.Width; c++)
            {
                var i = r * meta.Width + c;
                var v = (short)(data[2 * i] | (data[2 * i + 1] << 8));
                acc = c == 0 ? v : unchecked((short)(acc + v));
                h[i] = acc == meta.Nodata ? float.NaN : acc;
            }
        }
        return new PolarDem(meta.Width, meta.Height, meta.PixelM, meta.OriginX, meta.OriginY, h);
    }

    /// <summary>Planetocentric lat/lon (deg) → projected x, y (m), south polar stereographic on the sphere.</summary>
    public static (double X, double Y) Project(double latDeg, double lonDeg)
    {
        var lat = latDeg * Math.PI / 180;
        var lon = lonDeg * Math.PI / 180;
        var rho = 2 * MoonRadiusM * Math.Tan(Math.PI / 4 + lat / 2);
        return (rho * Math.Sin(lon), rho * Math.Cos(lon));
    }

    /// <summary>Unit vector (Moon-fixed, MOON_ME) → projected x, y, without trigonometry: x = 2R·Y/(1−Z), y = 2R·X/(1−Z).</summary>
    public static (double X, double Y) ProjectUnit(double ux, double uy, double uz)
    {
        var k = 2 * MoonRadiusM / (1 - uz);
        return (k * uy, k * ux);
    }

    /// <summary>Bilinear height (m) at projected x, y (pixel centres at +0.5), NaN outside or on no data.</summary>
    public double Sample(double x, double y)
    {
        var c = (x - OriginX) / PixelM - 0.5;
        var r = (OriginY - y) / PixelM - 0.5;
        var c0 = (int)Math.Floor(c);
        var r0 = (int)Math.Floor(r);
        if (c0 < 0 || r0 < 0 || c0 + 1 >= Width || r0 + 1 >= Height) return double.NaN;
        var fc = c - c0;
        var fr = r - r0;
        var i = r0 * Width + c0;
        var top = _h[i] * (1 - fc) + _h[i + 1] * fc;
        var bottom = _h[i + Width] * (1 - fc) + _h[i + Width + 1] * fc;
        return top * (1 - fr) + bottom * fr;   // NaN neighbours propagate
    }

    public double HeightAt(double latDeg, double lonDeg)
    {
        var (x, y) = Project(latDeg, lonDeg);
        return Sample(x, y);
    }

    /// <summary>Surface slope (deg), Horn's method on the 3×3 pixels around the nearest pixel; NaN at edges / no data.</summary>
    public double SlopeAt(double latDeg, double lonDeg)
    {
        var (x, y) = Project(latDeg, lonDeg);
        var col = (int)Math.Floor((x - OriginX) / PixelM);
        var row = (int)Math.Floor((OriginY - y) / PixelM);
        if (row < 1 || col < 1 || row >= Height - 1 || col >= Width - 1) return double.NaN;
        double Z(int dr, int dc) => _h[(row + dr) * Width + col + dc];
        var dzdx = ((Z(-1, 1) + 2 * Z(0, 1) + Z(1, 1)) - (Z(-1, -1) + 2 * Z(0, -1) + Z(1, -1))) / (8 * PixelM);
        var dzdy = ((Z(-1, -1) + 2 * Z(-1, 0) + Z(-1, 1)) - (Z(1, -1) + 2 * Z(1, 0) + Z(1, 1))) / (8 * PixelM);
        var s = Math.Atan(Math.Sqrt(dzdx * dzdx + dzdy * dzdy)) * 180 / Math.PI;
        return double.IsFinite(s) ? s : double.NaN;
    }

    /// <summary>True when the point is inside the custom-analysis area (80–90°S) and on valid terrain.</summary>
    public bool Covers(double latDeg, double lonDeg) => latDeg <= CoverageLatDeg && double.IsFinite(HeightAt(latDeg, lonDeg));

    /// <summary>
    /// Horizon elevation (deg) for azimuths <paramref name="first"/> … <paramref name="first"/>+count−1 (clockwise from north).
    /// Points along a ray are built as unit vectors on the sphere (cos δ · up + sin δ · direction), so the
    /// projection needs no trigonometry. Rays leaving the DEM keep the maximum found inside it; a ray
    /// without any valid sample falls back to the bare-sphere horizon (as in the pipeline).
    /// </summary>
    public void HorizonProfile(double latDeg, double lonDeg, double[] profile, int first = 0, int count = Azimuths,
                               double maxDistanceM = MaxDistanceM)
    {
        var ground = HeightAt(latDeg, lonDeg);
        if (!double.IsFinite(ground)) throw new ArgumentOutOfRangeException(nameof(latDeg), "the point is outside the DEM");
        var observer = ground + ObserverHeightM;
        var lat = latDeg * Math.PI / 180;
        var lon = lonDeg * Math.PI / 180;
        var (sl, cl, so, co) = (Math.Sin(lat), Math.Cos(lat), Math.Sin(lon), Math.Cos(lon));
        // Local frame: up, east, north.
        var (ux, uy, uz) = (cl * co, cl * so, sl);
        var (ex, ey) = (-so, co);
        var (nx, ny, nz) = (-sl * co, -sl * so, cl);
        var steps = (int)Math.Floor(maxDistanceM / PixelM + 1e-9);
        var bare = -Math.Acos(MoonRadiusM / (MoonRadiusM + ObserverHeightM)) * 180 / Math.PI;
        for (var a = first; a < first + count; a++)
        {
            var b = a * Math.PI / 180;
            var (cb, sb) = (Math.Cos(b), Math.Sin(b));
            var (dx, dy, dz) = (cb * nx + sb * ex, cb * ny + sb * ey, cb * nz);
            var best = double.NegativeInfinity;
            for (var k = 1; k <= steps; k++)
            {
                var d = k * PixelM;
                var delta = d / MoonRadiusM;
                var (cd, sd) = (Math.Cos(delta), Math.Sin(delta));
                var (px, py) = ProjectUnit(cd * ux + sd * dx, cd * uy + sd * dy, cd * uz + sd * dz);
                var h = Sample(px, py);
                if (double.IsNaN(h)) continue;
                var t = (h - observer - d * d / (2 * MoonRadiusM)) / d;   // tan(elevation)
                if (t > best) best = t;
            }
            profile[a] = double.IsNegativeInfinity(best) ? bare : Math.Atan(best) * 180 / Math.PI;
        }
    }
}

/// <summary><c>data/dem/south80_240m.json</c> (only what the app needs).</summary>
public sealed class DemMeta
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double PixelM { get; set; }
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public short Nodata { get; set; } = short.MinValue;
    public string Crs { get; set; } = "";
    public List<DemCheckPoint> CheckPoints { get; set; } = [];
}

public sealed class DemCheckPoint
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}
