using System.Text.Json;
using SeleneFrontend.Models;

namespace SeleneFrontend.Tests;

/// <summary>Science layer (<c>data/science/psr.json</c>, pipeline/build_psr.py): PSR distances and the sun-vs-ice trade-off.</summary>
public class PsrScienceTests
{
    private static readonly PsrScience Doc = Load();

    private static PsrScience Load()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "SeleneFrontend", "wwwroot", "data", "science", "psr.json");
            if (File.Exists(f)) return JsonSerializer.Deserialize<PsrScience>(File.ReadAllText(f), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
        throw new FileNotFoundException("psr.json not found");
    }

    [Fact]
    public void Every_site_has_distances_a_nearest_psr_and_outlines()
    {
        Assert.Equal(9, Doc.Sites.Count);
        Assert.Contains("Mazarico", Doc.Citation);
        Assert.StartsWith("https://", Doc.Url);
        foreach (var s in Doc.Sites)
        {
            Assert.False(s.OnPsr);
            Assert.True(s.DAnyKm <= s.D1Km && s.D1Km <= s.D10Km, s.SiteId);
            Assert.NotNull(s.Nearest1);
            Assert.True(s.Nearest1!.AreaKm2 >= 1);
            Assert.NotEmpty(s.Outlines);
            Assert.All(s.Outlines, o => Assert.True(o.Rings[0].Count >= 4 && o.Rings[0][0][1] is < -75 and > -90));
            Assert.NotNull(Doc.RegionFor(s.SiteId));
        }
    }

    [Fact]
    public void Ice_first_is_a_closer_front_candidate_with_enough_sunlight()
    {
        foreach (var r in Doc.Regions)
        {
            var site = Assert.Single(r.Candidates, c => c.IsSite);
            if (r.IceFirst() is not { } ice) continue;
            Assert.True(ice.Pareto && !ice.IsSite);
            Assert.True(ice.SunPct >= 40);
            Assert.True(ice.D1Km < site.D1Km, r.SiteId);
        }
        var nr2 = Doc.RegionFor("nobile_rim_2")!;
        Assert.Equal("NR201", nr2.IceFirst()!.CandidateId);                // 0.3 km from a PSR, 45 % sunlit (vs 52 %)
        Assert.Null(Doc.RegionFor("malapert_massif")!.IceFirst());          // the only closer front point is lit 16 %
    }

    [Fact]
    public void Plain_sentences_name_the_distance_and_the_trade_off()
    {
        var malapert = Doc.SiteFor("malapert_massif")!;
        Assert.Contains("10.5 km", PsrText.Sentence(malapert));
        Assert.Contains("water ice", PsrText.Sentence(malapert));
        var trade = PsrText.TradeOff(Doc.RegionFor("nobile_rim_2")!);
        Assert.NotNull(trade);
        Assert.Contains("NR201", trade);
        Assert.Contains("0.3 km", trade);
        Assert.Contains("instead of 52%", trade);
        Assert.Null(PsrText.TradeOff(Doc.RegionFor("malapert_massif")!));
    }
}
