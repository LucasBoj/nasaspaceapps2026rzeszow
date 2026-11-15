using System.Text.Json;
using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

/// <summary>Array power figures in landers.json that are derived from a published rating must stay consistent with the layout.</summary>
public class LanderPowerTests
{
    private static List<LanderPreset> Landers()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SeleneFrontend", "wwwroot", "data", "landers.json"))) d = d.Parent;
        return JsonSerializer.Deserialize<List<LanderPreset>>(
            File.ReadAllText(Path.Combine(d!.FullName, "SeleneFrontend", "wwwroot", "data", "landers.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    /// <summary>
    /// Team decision 2026-11-15: the FCC IM-1 "maximum of 788 W" is the summed rating of Nova-C's three fixed panels.
    /// solarArrayW is the output at the best polar Sun direction, i.e. rating × K of the layout (K ≈ 0.626 → 494 W).
    /// If someone changes the panel layout, this flags that the derived power must be recomputed too.
    /// </summary>
    [Fact]
    public void Nova_c_array_power_is_the_fcc_rating_times_the_layout_normalisation()
    {
        var nova = Landers().Single(l => l.Id == "nova_c");
        var k = PanelModel.For(nova.Panels)!.Normalization;
        Assert.Equal(ParamKind.Derived, nova.SolarArrayW!.Kind);
        Assert.Contains("fcc.gov", nova.SolarArrayW.SourceUrl);
        Assert.InRange(nova.SolarArrayW.Value, 788 * k - 1, 788 * k + 1);
    }
}
