using System.Text.Json;
using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

/// <summary>Sun vs ice, stage 3: the ice-first sites have full precomputed data and get a like-for-like verdict.</summary>
public class IceComparisonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Data()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "SeleneFrontend", "wwwroot", "data");
            if (File.Exists(Path.Combine(c, "science", "psr.json"))) return c;
        }
        throw new DirectoryNotFoundException("wwwroot/data not found");
    }

    private static readonly PsrScience Psr = JsonSerializer.Deserialize<PsrScience>(File.ReadAllText(Path.Combine(Data(), "science", "psr.json")), Web)!;

    private static IlluminationSeries Series(string id, params int[] years) =>
        IlluminationSeries.Concat(years.Select(y => JsonSerializer.Deserialize<IlluminationSeries>(
            File.ReadAllText(Path.Combine(Data(), "illumination", id, $"{y}.json")), Web)!).ToList());

    [Fact]
    public void Every_ice_site_has_full_data_and_matches_the_ice_first_rule()
    {
        var withIce = Psr.Regions.Where(r => r.IceSite is not null).ToList();
        Assert.True(withIce.Count >= 5, $"{withIce.Count} regions with an ice-first site");
        foreach (var r in withIce)
        {
            var ice = r.IceSite!;
            Assert.Equal(r.SiteId + "_ice", ice.SiteId);
            Assert.Equal(r.IceFirst()!.CandidateId, ice.CandidateId);          // the app's rule = the pipeline's choice
            var dir = Path.Combine(Data(), "illumination", ice.SiteId);
            foreach (var f in new[] { "2027.json", "2035.json", "ellipse.json", "heights.json" })
                Assert.True(File.Exists(Path.Combine(dir, f)), $"{ice.SiteId}/{f}");
            var s = Series(ice.SiteId, 2027);
            Assert.Equal(ice.SiteId, s.SiteId);
            Assert.Equal(ice.Lat, s.Lat, 4);
            Assert.True(s.HasSunGeometry && s.EarthAzCentiDeg.Length == s.Count);
        }
        foreach (var r in Psr.Regions.Where(r => r.IceSite is null)) Assert.Null(r.IceFirst());
    }

    [Fact]
    public void Same_lander_and_date_at_the_ice_site_is_a_real_simulation()
    {
        var region = Psr.RegionFor("nobile_rim_2")!;
        var lander = new LanderInputs(400, 600, 300, 120, ParamKind.Assumption, BatteryRoundTripEff: 0.9);
        var t = new MissionThresholds(20, 50);
        var sun = Series("nobile_rim_2", 2028);
        var ice = Series(region.IceSite!.SiteId, 2028);
        var landing = new DateTime(2028, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var nominal = MissionSimulator.Run(sun, lander, landing, 7, t);
        var same = IceComparison.SameDate(ice, lander, nominal, t);
        Assert.Equal(nominal.LandingUtc, same.LandingUtc);
        Assert.Equal(nominal.Steps, same.Steps);
        Assert.Equal(MissionSimulator.Run(ice, lander, landing, 7, t, recordSeries: false).Verdict, same.Verdict);

        var best = IceComparison.BestInWindowAsync(new OptimizerSite(region.IceSite.SiteId, region.IceSite.Name, ice), lander,
            new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2028, 12, 1, 0, 0, 0, DateTimeKind.Utc), 7, t).GetAwaiter().GetResult();
        Assert.NotNull(best);
        Assert.True(best!.Verdict <= same.Verdict || best.MinSocPct >= same.MinSocPct - 1e-9, "the best date is at least as good");

        var r = new IceComparisonResult("Nobile Rim 2", nominal, region.IceSite, same, best, region.Site,
            region.Candidates.Single(c => c.CandidateId == region.IceSite.CandidateId));
        var text = IceComparison.Sentence(r);
        Assert.StartsWith("Same lander and date at NR201, 0.3 km from the ice: ", text);
        Assert.Contains(VerdictText.Label(same.Verdict).TrimStart('✓', '⚠', '✕', ' ').Split(' ')[0], text);
    }
}
