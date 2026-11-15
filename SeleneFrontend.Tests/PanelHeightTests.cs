using System.Text.Json;
using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

/// <summary>
/// Panel height (<c>illumination/{siteId}/heights.json</c>, <see cref="PanelHeightAnalysis"/>): the site's horizon for
/// 0–10 m observers; the mission is re-run at 1 m and 3 m. The 2 m entry must reproduce the shipped series.
/// </summary>
public class PanelHeightTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string Illum = FindIllum();
    private static readonly MissionThresholds Thresholds = new(20, 50);

    private static string FindIllum()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "SeleneFrontend", "wwwroot", "data", "illumination");
            if (Directory.Exists(c)) return c;
        }
        throw new DirectoryNotFoundException("illumination data not found");
    }

    private static IlluminationSeries Year(string site, int y) =>
        JsonSerializer.Deserialize<IlluminationSeries>(File.ReadAllText(Path.Combine(Illum, site, $"{y}.json")), Web)!;

    private static HorizonHeightsData Heights(string site) =>
        JsonSerializer.Deserialize<HorizonHeightsData>(File.ReadAllText(Path.Combine(Illum, site, "heights.json")), Web)!;

    public static TheoryData<string> SiteIds() =>
        new(JsonSerializer.Deserialize<IlluminationIndex>(File.ReadAllText(Path.Combine(Illum, "index.json")), Web)!.Sites.Select(s => s.SiteId));

    [Theory]
    [MemberData(nameof(SiteIds))]
    public void Every_site_has_heights_whose_2m_mask_is_the_series_horizon(string siteId)
    {
        var h = Heights(siteId);
        var site = Year(siteId, 2027);
        Assert.Equal(siteId, h.SiteId);
        Assert.Equal(site.ObserverHeightM, h.SeriesObserverHeightM);
        foreach (var m in new[] { 0.0, PanelHeightAnalysis.LowM, 2.0, PanelHeightAnalysis.HighM }) Assert.NotNull(h.At(m));
        Assert.Equal(site.HorizonDeg.Select(x => (int)Math.Round(x * 100)), h.At(2)!.HorizonCentiDeg);
        // A higher observer never sees a higher horizon (same terrain, the eye is lifted): sunlit time cannot drop.
        var ordered = h.Heights.OrderBy(x => x.HeightM).ToList();
        for (var i = 1; i < ordered.Count; i++) Assert.True(ordered[i].PctSunlit >= ordered[i - 1].PctSunlit - 0.1, $"{siteId}: {ordered[i].HeightM} m");
    }

    [Fact]
    public void The_2m_case_reproduces_the_nominal_mission_and_heights_order_the_sunlight()
    {
        var site = IlluminationSeries.Concat([Year("nobile_rim_1", 2028), Year("nobile_rim_1", 2029)]);
        var lander = new LanderInputs(400, 600, 300, 120, ParamKind.Assumption, BatteryRoundTripEff: 0.9);
        var nominal = MissionSimulator.Run(site, lander, new DateTime(2028, 12, 26, 0, 0, 0, DateTimeKind.Utc), 7, Thresholds);
        var data = Heights("nobile_rim_1");

        var same = PanelHeightAnalysis.For(site, data, lander, nominal, Thresholds, 2, 2)!;
        Assert.Equal(nominal.Verdict, same.Low.Verdict);
        Assert.InRange(same.Low.SunPct, nominal.SunPct - 2, nominal.SunPct + 2);       // rebuilt from rounded angles
        Assert.InRange(same.Low.MinSocPct, nominal.MinSocPct - 3, nominal.MinSocPct + 3);

        var s = PanelHeightAnalysis.For(site, data, lander, nominal, Thresholds)!;
        Assert.Equal(PanelHeightAnalysis.LowM, s.Low.HeightM);
        Assert.Equal(PanelHeightAnalysis.HighM, s.High.HeightM);
        Assert.True(s.Low.SunPct <= s.High.SunPct, $"{s.Low.SunPct} vs {s.High.SunPct}");
        Assert.True(s.Low.MinSocPct <= s.High.MinSocPct + 1e-9);
        Assert.Equal(s.Low.Verdict == nominal.Verdict && s.High.Verdict == nominal.Verdict, s.Robust);
    }

    [Fact]
    public void Missing_heights_give_no_result()
    {
        var site = Year("nobile_rim_1", 2028);
        var lander = new LanderInputs(400, 600, 300, 120, ParamKind.Assumption);
        var nominal = MissionSimulator.Run(site, lander, new DateTime(2028, 3, 1, 0, 0, 0, DateTimeKind.Utc), 7, Thresholds);
        Assert.Null(PanelHeightAnalysis.For(site, new HorizonHeightsData(), lander, nominal, Thresholds));
    }
}
