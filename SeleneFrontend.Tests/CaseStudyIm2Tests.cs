using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

public class CaseStudyIm2Tests
{
    private static PanelLayout Layout(params (string Name, double Az, double Elev)[] facets) => new()
    {
        Mode = PanelLayout.FixedMode,
        Facets = facets.Select(f => new PanelFacet { Name = f.Name, AzOffsetDeg = f.Az, NormalElevDeg = f.Elev, Share = 1 }).ToList(),
    };

    // Nova-C-like: deck + two walls at ±30° (landers.json, team description).
    private static readonly PanelLayout NovaC = Layout(("deck", 0, 90), ("wall A", -30, 0), ("wall B", 30, 0));

    private static PanelFacet F(PanelLayout l, string name) => l.Facets.Single(f => f.Name == name);

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(225)]
    public void Deck_ends_horizontal_facing_the_fall_direction(double fallAz)
    {
        var t = CaseStudyIm2.Tipped(Layout(("deck", 0, 90)), fallAz, headingDeg: 40);
        Assert.Equal(0, F(t, "deck").NormalElevDeg, 6);
        Assert.Equal(fallAz, F(t, "deck").AzOffsetDeg, 6);
    }

    [Fact]
    public void Wall_facing_the_fall_points_down_and_the_opposite_wall_up()
    {
        // Heading 0: wall "front" faces north (az 0); falling north puts it face-down, the back wall face-up.
        var t = CaseStudyIm2.Tipped(Layout(("front", 0, 0), ("back", 180, 0), ("side", 90, 0)), fallAzDeg: 0, headingDeg: 0);
        Assert.Equal(-90, F(t, "front").NormalElevDeg, 6);
        Assert.Equal(90, F(t, "back").NormalElevDeg, 6);
        // The side wall's normal is along the rotation axis: unchanged (horizontal, east).
        Assert.Equal(0, F(t, "side").NormalElevDeg, 6);
        Assert.Equal(90, F(t, "side").AzOffsetDeg, 6);
    }

    [Fact]
    public void Tilt_zero_keeps_the_geometry_and_tipping_keeps_k()
    {
        var same = CaseStudyIm2.Tipped(NovaC, fallAzDeg: 123, headingDeg: 0, tiltDeg: 0);
        Assert.Equal(30, F(same, "wall B").AzOffsetDeg, 6);
        Assert.Equal(330, F(same, "wall A").AzOffsetDeg, 6);
        Assert.Equal(90, F(same, "deck").NormalElevDeg, 6);

        var k = PanelModel.For(NovaC)!.Normalization;
        var tipped = CaseStudyIm2.Tipped(NovaC, fallAzDeg: 77, headingDeg: 15);
        Assert.Equal(k, PanelModel.For(tipped)!.Normalization, 12);
    }

    [Fact]
    public void Lying_on_its_side_produces_less_than_upright_facing_the_sun()
    {
        // Low Sun (3°) at a fixed azimuth for 48 h; one vertical wall panel. Upright AUTO aims it at the Sun.
        var n = 48;
        var s = SolarGeometryTests.GeoSeries(Enumerable.Repeat(100, n).ToArray(), Enumerable.Repeat(3.0, n).ToArray(),
                                             Enumerable.Repeat(200.0, n).ToArray());
        var wall = Layout(("wall", 0, 0));
        var lander = new LanderInputs(100, 500, 40, 20, ParamKind.Derived, wall);
        var thresholds = new MissionThresholds(20, 0);

        var up = CaseStudyIm2.Upright(s, lander, Fixtures.T0, 1, thresholds, windowHours: 12, headingSteps: 36);
        var side = CaseStudyIm2.Tipped(s, lander, Fixtures.T0, 1, thresholds, windowHours: 12, fallSteps: 8, headingSteps: 4);

        Assert.Equal(36, up.Orientations);
        Assert.Equal(32, side.Orientations);
        Assert.True(side.WindowEnergyWh.Min <= side.WindowEnergyWh.Median && side.WindowEnergyWh.Median <= side.WindowEnergyWh.Max);
        Assert.True(up.WindowEnergyWh.Max >= side.WindowEnergyWh.Max - 1e-6);
        // Best heading (200°, on the 10° grid) aims the wall at the Sun: 100 W × cos 3° / K (K = 1) × 12 h ≈ 1198 Wh;
        // the worst heading faces it away: 0 Wh.
        Assert.Equal(1200 * Math.Cos(3 * Math.PI / 180), up.WindowEnergyWh.Max, 1);
        Assert.Equal(0, up.WindowEnergyWh.Min, 6);
        Assert.Equal(24, side.PowerMax.Length);                   // a 1-day mission: the envelope stops at its 24 steps
        Assert.True(side.PowerMin.Zip(side.PowerMax).All(p => p.First <= p.Second));
    }

    [Fact]
    public void Dark_crater_runs_on_the_battery_alone()
    {
        // 200 Wh, no Sun: survival load 20 W empties it in the 10th hour (index 9, the simulator's depletion step,
        // as in MissionSimulatorTests) → 9 h survived; day load 40 W → 4 h (ideal battery).
        var n = 48;
        var s = SolarGeometryTests.GeoSeries(Enumerable.Repeat(100, n).ToArray(), Enumerable.Repeat(3.0, n).ToArray(),
                                             Enumerable.Repeat(200.0, n).ToArray());
        var lander = new LanderInputs(100, 200, 40, 20, ParamKind.Derived, Layout(("wall", 0, 0)));
        var d = CaseStudyIm2.Dark(s, lander, Fixtures.T0, 1, new MissionThresholds(20, 0));
        Assert.Equal(9, d.SurvivalLoadHours, 9);
        Assert.Equal(4, d.DayLoadHours, 9);
        Assert.All(CaseStudyIm2.WithoutSun(s).SunFraction, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Window_energy_counts_partial_hours()
    {
        var r = new SimulationResult { StepHours = 1, Steps = 3, PowerW = [10, 20, 30] };
        Assert.Equal(10 + 20 + 0.5 * 30, CaseStudyIm2.WindowEnergy(r, 2.5), 9);
        Assert.Equal(3, CaseStudyIm2.Survived(r), 9);
    }
}
