using System.Text.Json;
using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

/// <summary>
/// Landing ellipse (<c>illumination/{siteId}/ellipse.json</c>, <see cref="LandingEllipse"/>): the shown mission re-run over
/// a 60 m grid inside the lander's landing accuracy. The reconstruction itself is validated in the pipeline against a full
/// recomputation (pipeline/docs/ellipse_validation.json: ≤ 2 pp); these tests pin the C# port to the same rules.
/// </summary>
public class LandingEllipseTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string WwwRoot = FindWwwRoot();
    private static readonly string Illum = Path.Combine(WwwRoot, "data", "illumination");
    private static readonly MissionThresholds Thresholds = new(20, 50);

    private static string FindWwwRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "SeleneFrontend", "wwwroot");
            if (Directory.Exists(Path.Combine(c, "data", "illumination"))) return c;
        }
        throw new DirectoryNotFoundException("wwwroot not found");
    }

    private static IlluminationSeries Year(string site, int y) =>
        JsonSerializer.Deserialize<IlluminationSeries>(File.ReadAllText(Path.Combine(Illum, site, $"{y}.json")), Web)!;

    private static LandingEllipseData Ellipse(string site) =>
        JsonSerializer.Deserialize<LandingEllipseData>(File.ReadAllText(Path.Combine(Illum, site, "ellipse.json")), Web)!;

    private static List<LanderPreset> Landers() =>
        JsonSerializer.Deserialize<List<LanderPreset>>(File.ReadAllText(Path.Combine(WwwRoot, "data", "landers.json")), Web)!;

    public static TheoryData<string> SiteIds() =>
        new(JsonSerializer.Deserialize<IlluminationIndex>(File.ReadAllText(Path.Combine(Illum, "index.json")), Web)!.Sites.Select(s => s.SiteId));

    [Theory]
    [MemberData(nameof(SiteIds))]
    public void Every_site_has_an_ellipse_whose_centre_is_the_site(string siteId)
    {
        var e = Ellipse(siteId);
        var site = Year(siteId, 2027);
        Assert.Equal(siteId, e.SiteId);
        Assert.Equal(60, e.SpacingM);
        Assert.True(e.RadiusM >= Landers().Max(l => l.LandingAccuracyM?.Value ?? 0), "grid must cover every lander's radius");
        Assert.True(e.Points.Count >= 190, $"{e.Points.Count} points");          // 197 unless a point fell off the DEM
        var centre = Assert.Single(e.Points, p => p.DistanceM < 1e-6);
        Assert.Equal(0, centre.AzShiftDeg, 6);
        Assert.Equal(0, centre.TiltDeg, 6);
        Assert.Equal(site.HorizonDeg.Select(h => (int)Math.Round(h * 100)), centre.HorizonCentiDeg);
        Assert.All(e.Points, p => Assert.Equal(360, p.HorizonCentiDeg.Length));
        Assert.All(e.Points, p => Assert.InRange(p.TiltDeg, 0, 0.017));         // ≤ 480 m / R = 0.0158°
    }

    [Fact]
    public void Centre_point_reconstruction_matches_the_shipped_series_within_rounding()
    {
        var site = Year("malapert_massif", 2027);
        var centre = Ellipse("malapert_massif").Points.Single(p => p.DistanceM < 1e-6);
        var s = LandingEllipse.PointSeries(site, centre, 0, site.Count);
        var maxDiff = site.SunFraction.Zip(s.SunFraction, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(maxDiff <= 2, $"max |Δ sunFraction| {maxDiff} pp");          // file values come from full precision
        var earthDiff = site.EarthVisible.Zip(s.EarthVisible, (a, b) => a != b ? 1 : 0).Sum();
        Assert.True(earthDiff <= 10, $"{earthDiff} hours of Earth visibility differ");
    }

    [Fact]
    public void Horizon_lookup_applies_shift_and_tilt()
    {
        var mask = Enumerable.Range(0, 360).Select(a => a * 10).ToArray();      // 0.1° per degree of azimuth
        var p = new EllipsePoint { HorizonCentiDeg = mask, AzShiftDeg = 0.5, TiltDeg = 0.01, TiltAzDeg = 90 };
        static double Tilt(double az) => 0.01 * Math.Cos((az - 90) * Math.PI / 180);
        Assert.Equal(10.05 - Tilt(100), LandingEllipse.HorizonAt(p, 100), 9);          // mask at 100.5° in the point's frame
        Assert.Equal(0.01 - Tilt(359.6), LandingEllipse.HorizonAt(p, 359.6), 9);       // 360.1° wraps to 0.1°
    }

    [Fact]
    public void A_walled_in_point_is_never_lit_and_an_open_point_always_is()
    {
        var site = Year("malapert_massif", 2027);
        var wall = new EllipsePoint { E = 60, HorizonCentiDeg = Enumerable.Repeat(4000, 360).ToArray() };
        var open = new EllipsePoint { E = -60, HorizonCentiDeg = Enumerable.Repeat(-2000, 360).ToArray() };
        Assert.All(LandingEllipse.PointSeries(site, wall, 100, 500).SunFraction, f => Assert.Equal(0, f));
        Assert.All(LandingEllipse.PointSeries(site, wall, 100, 500).EarthVisible, v => Assert.Equal(0, v));
        Assert.All(LandingEllipse.PointSeries(site, open, 100, 500).SunFraction, f => Assert.Equal(100, f));
    }

    [Fact]
    public void Analyze_counts_the_grid_inside_the_radius_and_keeps_the_nominal_verdict_at_the_target()
    {
        var site = IlluminationSeries.Concat([Year("nobile_rim_1", 2027), Year("nobile_rim_1", 2028)]);
        var nova = Landers().Single(l => l.Id == "nova_c");
        var lander = new LanderInputs(nova.SolarArrayW!.Value, nova.BatteryWh!.Value, nova.DayLoadW!.Value, nova.SurvivalLoadW!.Value,
            ParamKind.Assumption, nova.Panels, BatteryRoundTripEff: nova.BatteryRoundTripEff!.Value);
        var nominal = MissionSimulator.Run(site, lander, new DateTime(2027, 12, 1, 0, 0, 0, DateTimeKind.Utc), 7, Thresholds);
        var e = Ellipse("nobile_rim_1");

        var r400 = LandingEllipse.Analyze(site, e, lander, nominal, Thresholds, 400)!;
        Assert.Equal(e.Points.Count(p => p.DistanceM <= 400 + 1e-6), r400.Count);   // 137 on a full grid
        Assert.Equal(nominal.Verdict, r400.Points.Single(p => p.E == 0 && p.N == 0).Verdict);
        Assert.Equal(r400.Count, r400.Feasible + r400.Marginal + r400.NotFeasible);
        Assert.InRange(r400.FeasibleShare, 0, 1);

        var r100 = LandingEllipse.Analyze(site, e, lander, nominal, Thresholds, 100)!;
        Assert.Equal(9, r100.Count);                                                 // target + 4 at 60 m + 4 at 85 m
        Assert.Equal(e.RadiusM, LandingEllipse.Analyze(site, e, lander, nominal, Thresholds, 5000)!.RadiusM);   // clamped to the grid

        // The re-run uses the mission's heading, not a new AUTO heading per point.
        Assert.NotNull(nominal.HeadingDeg);
        var moved = r400.Points.First(p => p.E != 0 || p.N != 0);
        var point = e.Points.First(p => p.E == moved.E && p.N == moved.N);
        var series = LandingEllipse.PointSeries(site, point, site.IndexOf(nominal.LandingUtc), nominal.Steps);
        var rerun = MissionSimulator.Run(series, lander with { HeadingDeg = nominal.HeadingDeg }, series.Start, 7, Thresholds, recordSeries: false);
        Assert.Equal(rerun.Verdict, moved.Verdict);
        Assert.Equal(rerun.MinSocPct, moved.MinSocPct, 9);
    }

    [Fact]
    public void Every_lander_has_a_landing_accuracy_with_provenance()
    {
        foreach (var l in Landers())
        {
            var acc = l.LandingAccuracyM;
            Assert.NotNull(acc);
            Assert.InRange(acc!.Value, 50, 480);
            Assert.Equal("m", acc.Unit);
            Assert.False(string.IsNullOrWhiteSpace(acc.Note), l.Id);
            if (acc.Kind != ParamKind.Assumption) Assert.StartsWith("https://", acc.SourceUrl);
        }
    }

    [Fact]
    public void Plain_sentence_depends_on_the_nominal_verdict_and_the_share()
    {
        static EllipsePointResult P(Verdict v) => new(0, 0, v, 50, 100, 100, 2);
        var all = new EllipseResult(400, 60, [P(Verdict.Feasible), P(Verdict.Feasible)]);
        var some = new EllipseResult(400, 60, [P(Verdict.Feasible), P(Verdict.NotFeasible), P(Verdict.NotFeasible), P(Verdict.Feasible)]);
        var none = new EllipseResult(100, 60, [P(Verdict.NotFeasible)]);
        Assert.Contains("every spot", VerdictText.EllipseSentence(all, Verdict.Feasible));
        Assert.Contains("50% of the spots", VerdictText.EllipseSentence(some, Verdict.Feasible));
        Assert.Contains("400 m", VerdictText.EllipseSentence(some, Verdict.Feasible));
        Assert.Contains("50% of the spots within 400 m", VerdictText.EllipseSentence(some, Verdict.NotFeasible));
        Assert.Contains("no spot", VerdictText.EllipseSentence(none, Verdict.NotFeasible));
    }
}
