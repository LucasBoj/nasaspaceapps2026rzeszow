using System.Text.Json;
using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

/// <summary>Data contract 2026-11-14: one file per site and year (2026-11 … 2035), joined by the repository.</summary>
public class YearlyDataTests
{
    private static readonly string Illum = Path.Combine(FindWwwRoot(), "data", "illumination");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

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

    [Fact]
    public void Index_covers_2026_11_to_2035_with_yearly_files_for_every_site()
    {
        var index = JsonSerializer.Deserialize<IlluminationIndex>(File.ReadAllText(Path.Combine(Illum, "index.json")), Web)!;
        Assert.Equal(new DateTime(2026, 11, 1), index.Start.Date);
        Assert.Equal(new DateTime(2035, 12, 31), index.End.Date);
        Assert.Equal(Enumerable.Range(2026, 10), index.Years);
        Assert.Equal(9, index.Sites.Count);
        foreach (var s in index.Sites)
            foreach (var y in index.Years)
                Assert.True(File.Exists(Path.Combine(Illum, s.SiteId, $"{y}.json")), $"{s.SiteId}/{y}.json");
    }

    [Fact]
    public void Year_boundaries_are_exact_and_files_join_without_gaps()
    {
        var y26 = Year("nobile_rim_1", 2026);
        var y27 = Year("nobile_rim_1", 2027);
        var y28 = Year("nobile_rim_1", 2028);
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), TimeUtil.AsUtc(y26.Start));
        Assert.Equal(61 * 24, y26.Count);                // Nov + Dec 2026
        Assert.Equal(365 * 24, y27.Count);
        Assert.Equal(366 * 24, y28.Count);               // leap year
        var joined = IlluminationSeries.Concat([y26, y27, y28]);
        Assert.Equal(y26.Count + y27.Count + y28.Count, joined.Count);
        Assert.Equal(joined.Count, joined.SunAzCentiDeg.Length);
        // The first hour of 2028 in the joined series is the first sample of the 2028 file.
        var i = joined.IndexOf(new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(y28.SunFraction[0], joined.SunFraction[i]);
        Assert.Equal(y28.EarthElevCentiDeg[0], joined.EarthElevCentiDeg[i]);
    }

    [Fact]
    public void Gaps_between_yearly_files_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => IlluminationSeries.Concat([Year("haworth", 2027), Year("haworth", 2029)]));
    }

    [Theory]
    [InlineData("2027-01-01", "2028-12-31", 2027, 2029)]   // mission tail after 31 Dec 2028 needs early 2029
    [InlineData("2027-01-01", "2027-06-30", 2027, 2027)]
    [InlineData("2035-01-01", "2035-12-31", 2035, 2035)]   // clipped to the last year of data
    [InlineData("2026-11-14", "2027-10-01", 2026, 2027)]
    public void Only_the_years_of_the_window_are_loaded(string from, string to, int first, int last)
    {
        var years = Enumerable.Range(2026, 10).ToList();
        Assert.Equal((first, last), IlluminationRepository.YearsFor(DateTime.Parse(from), DateTime.Parse(to), years));
    }
}
