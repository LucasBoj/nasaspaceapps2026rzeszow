using System.Globalization;
using System.Text;
using System.Text.Json;
using SeleneFrontend.Models;
using Xunit.Abstractions;

namespace SeleneFrontend.Tests;

/// <summary>
/// In-browser analysis of any point in 80–90°S: projection, horizon mask on synthetic terrain, Sun/Earth geometry
/// against JPL Horizons, and the whole method against the pipeline's data for the 9 regions.
/// </summary>
public class CustomSiteTests(ITestOutputHelper output)
{
    private static readonly string Repo = FindRepo();
    private static readonly string Data = Path.Combine(Repo, "SeleneFrontend", "wwwroot", "data");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<PolarDem> Dem = new(LoadDem);
    private static readonly Lazy<DemMeta> Meta = new(() => JsonSerializer.Deserialize<DemMeta>(File.ReadAllText(Path.Combine(Data, "dem", "south80_240m.json")), Web)!);

    private static string FindRepo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SeleneFrontend", "wwwroot", "data", "sites.json"))) return d.FullName;
        throw new DirectoryNotFoundException("repo root not found");
    }

    private static PolarDem LoadDem() => PolarDem.FromBinary(Meta.Value, File.ReadAllBytes(Path.Combine(Data, "dem", "south80_240m.bin")));

    private static MoonEphemerisYear Ephem(int year)
    {
        var start = year == 2026 ? new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) : new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return MoonEphemerisYear.FromBinary(start, File.ReadAllBytes(Path.Combine(Data, "ephem", $"{year}.bin")));
    }

    // ------------------------------------------------------------------ projection and synthetic terrain

    [Fact]
    public void Projection_matches_pyproj_check_points()
    {
        foreach (var p in Meta.Value.CheckPoints)
        {
            var (x, y) = PolarDem.Project(p.Lat, p.Lon);
            Assert.True(Math.Abs(x - p.X) < 0.01 && Math.Abs(y - p.Y) < 0.01, $"({p.Lat}, {p.Lon}): {x:F3},{y:F3} vs {p.X:F3},{p.Y:F3}");
            // The trig-free unit-vector form used along horizon rays gives the same point.
            var (lat, lon) = (p.Lat * Math.PI / 180, p.Lon * Math.PI / 180);
            var (ux, uy) = PolarDem.ProjectUnit(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
            Assert.True(Math.Abs(ux - p.X) < 0.01 && Math.Abs(uy - p.Y) < 0.01);
        }
    }

    private static PolarDem Synthetic(Func<double, double, float> height, int n = 1400, double px = 240)
    {
        var h = new float[n * n];
        var origin = n * px / 2;
        for (var r = 0; r < n; r++)
            for (var c = 0; c < n; c++)
                h[r * n + c] = height(-origin + (c + 0.5) * px, origin - (r + 0.5) * px);
        return new PolarDem(n, n, px, -origin, origin, h);
    }

    [Fact]
    public void Flat_terrain_gives_a_horizon_at_zero()
    {
        var dem = Synthetic((_, _) => 0f);
        var profile = new double[360];
        dem.HorizonProfile(-89.0, 30.0, profile);
        // Only the 2 m observer and the Moon's curvature: the horizon dips a few hundredths of a degree.
        Assert.All(profile, h => Assert.InRange(h, -0.1, 0.0));
    }

    [Fact]
    public void A_wall_of_known_height_gives_the_known_angle()
    {
        // Pole-centred plateau: terrain 0 m, everything farther than 6 km from the pole is 600 m high.
        var dem = Synthetic((x, y) => Math.Sqrt(x * x + y * y) > 6000 ? 600f : 0f);
        var profile = new double[360];
        dem.HorizonProfile(-90.0, 0.0, profile);
        // First fully-inside sample is 6 km + up to one pixel away: atan((600 − 2 − d²/2R) / d) with d ≈ 6.0–6.5 km.
        double Expected(double d) => Math.Atan((600 - 2 - d * d / (2 * PolarDem.MoonRadiusM)) / d) * 180 / Math.PI;
        Assert.All(profile, h => Assert.InRange(h, Expected(6600), Expected(5900)));
        Assert.InRange(profile.Max() - profile.Min(), 0, 0.5);   // the same in every direction
    }

    // ------------------------------------------------------------------ Sun / Earth geometry vs JPL Horizons

    public static TheoryData<string, string> HorizonsFiles() => new()
    {
        { "nobile_rim_1", "sun" }, { "nobile_rim_1", "earth" },
        { "haworth", "sun" }, { "haworth", "earth" },
        { "slater_plain", "sun" }, { "slater_plain", "earth" },
    };

    [Theory]
    [MemberData(nameof(HorizonsFiles))]
    public void Sun_and_Earth_direction_match_JPL_Horizons(string site, string body)
    {
        var lines = File.ReadAllLines(Path.Combine(Repo, "pipeline", "data", "horizons", $"{site}_{body}.txt"));
        var geo = lines.First(l => l.StartsWith("Center geodetic", StringComparison.Ordinal)).Split(':')[1].Split('{')[0].Split(',');
        var lon = double.Parse(geo[0], CultureInfo.InvariantCulture);
        var lat = double.Parse(geo[1], CultureInfo.InvariantCulture);
        var rows = lines.SkipWhile(l => l != "$$SOE").Skip(1).TakeWhile(l => l != "$$EOE").ToList();
        Assert.True(rows.Count >= 40);
        var years = new Dictionary<int, MoonEphemerisYear>();
        var (maxEl, maxAz) = (0.0, 0.0);
        foreach (var row in rows)
        {
            var parts = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var t = DateTime.ParseExact($"{parts[0]} {parts[1]}", "yyyy-MMM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            var az = double.Parse(parts[^2], CultureInfo.InvariantCulture);
            var el = double.Parse(parts[^1], CultureInfo.InvariantCulture);
            if (!years.TryGetValue(t.Year, out var eph)) years[t.Year] = eph = Ephem(t.Year);
            var s = CustomSiteMath.NewSeries("x", lat, lon, new double[360], t, 1);
            CustomSiteMath.Fill(s, eph, (int)Math.Round((t - eph.Start).TotalHours), 0, 1);
            var (myEl, myAz) = body == "sun" ? (s.SunElevCentiDeg[0] / 100.0, s.SunAzCentiDeg[0] / 100.0) : (s.EarthElevCentiDeg[0] / 100.0, s.EarthAzCentiDeg[0] / 100.0);
            maxEl = Math.Max(maxEl, Math.Abs(myEl - el));
            maxAz = Math.Max(maxAz, Math.Abs(((myAz - az) % 360 + 540) % 360 - 180));
        }
        output.WriteLine($"{site} {body}: max |Δel| {maxEl:F4}°, max |Δaz| {maxAz:F4}° over {rows.Count} epochs");
        // Float32 sub-points + 0.01° storage: well inside 0.02°; the Earth margin is 0.5°, the Sun's radius 0.27°.
        Assert.True(maxEl < 0.02 && maxAz < 0.02, $"Δel {maxEl:F4}°, Δaz {maxAz:F4}°");
    }

    // ------------------------------------------------------------------ whole method vs the pipeline (9 regions)

    [Fact]
    public void Browser_method_matches_the_pipeline_for_the_9_regions()
    {
        var sites = JsonSerializer.Deserialize<List<Site>>(File.ReadAllText(Path.Combine(Data, "sites.json")), Web)!;
        var index = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(Data, "illumination", "index.json")));
        var y27 = Ephem(2027);
        var y28 = Ephem(2028);
        var table = new StringBuilder("| Region | Sunlit % pipeline (120 m) | browser (240 m) | Δ pp | Earth % pipeline | browser | Δ pp | Longest dark h pipeline | browser | horizon ms |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n");
        var worst = 0.0;
        foreach (var site in sites)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var profile = new double[360];
            Dem.Value.HorizonProfile(site.Lat, site.Lon, profile);
            var ms = sw.ElapsedMilliseconds;
            var s = CustomSiteMath.NewSeries(site.Id, site.Lat, site.Lon, profile, y27.Start, y27.Count + y28.Count);
            CustomSiteMath.Fill(s, y27, 0, 0, y27.Count);
            CustomSiteMath.Fill(s, y28, 0, y27.Count, s.Count);
            var lit = s.SunFraction.Select(f => f > 0).ToArray();   // index.json definition: any part of the disk
            var sun = 100.0 * lit.Count(x => x) / s.Count;
            var earth = 100.0 * s.EarthVisible.Sum() / s.Count;
            var dark = LongestRun(lit.Select(x => !x).ToArray());

            var entry = index.GetProperty("sites").EnumerateArray().First(e => e.GetProperty("siteId").GetString() == site.Id);
            double Year(string y, string k) => entry.GetProperty("byYear").GetProperty(y).GetProperty(k).GetDouble();
            var pSun = (Year("2027", "pctSunlit") * 8760 + Year("2028", "pctSunlit") * 8784) / 17544;
            var pEarth = (Year("2027", "pctComms") * 8760 + Year("2028", "pctComms") * 8784) / 17544;
            var pDark = Math.Max(Year("2027", "longestDarkHours"), Year("2028", "longestDarkHours"));
            worst = Math.Max(worst, Math.Abs(sun - pSun));
            table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {site.Name} | {pSun:F1} | {sun:F1} | {sun - pSun:+0.0;−0.0;0.0} | {pEarth:F1} | {earth:F1} | {earth - pEarth:+0.0;−0.0;0.0} | {pDark:F0} | {dark:F0} | {ms} |"));
            Assert.True(Math.Abs(sun - pSun) <= 5, $"{site.Id}: sunlit {sun:F1}% vs pipeline {pSun:F1}%");
        }
        output.WriteLine(table.ToString());
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "custom-site-validation.md"), table.ToString());
        output.WriteLine($"worst |Δ sunlit| = {worst:F1} pp");
    }

    private static int LongestRun(bool[] a)
    {
        int best = 0, run = 0;
        foreach (var x in a) { run = x ? run + 1 : 0; best = Math.Max(best, run); }
        return best;   // longest dark run within 2027–2028 (index.json keeps the per-year maximum)
    }
}
