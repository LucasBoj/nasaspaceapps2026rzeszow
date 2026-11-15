using System.Text.Json;
using SeleneFrontend.Models;
using SeleneFrontend.Services;

namespace SeleneFrontend.Tests;

/// <summary>Warm-box heater model (values computed by hand) and its LRO Diviner ground input.</summary>
public class ThermalModelTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // 300 W radiated at +20 °C, set point −20 °C, half the area sees the ground.
    private static ThermalInputs Box(double capacityJPerK = 0) => new(300, capacityJPerK, 20, -20, 0.5);

    // ---- Heat balance by hand: G = 300 / 293.15⁴ = 4.0622e-8 W/K⁴ ----

    [Fact]
    public void Loss_at_the_set_point_scales_with_T4()
    {
        // (253.15 / 293.15)⁴ = 0.556098 → 166.83 W to a 0 K sky
        Assert.Equal(166.83, ThermalModel.LossW(Box(), 253.15, 0), 2);
        // ground at 100 K, F = 0.5: G · 0.5 · 100⁴ = 2.03 W back
        Assert.Equal(164.80, ThermalModel.LossW(Box(), 253.15, 100), 2);
    }

    [Fact]
    public void Heaters_cover_only_what_the_electronics_do_not()
    {
        Assert.Equal(46.83, ThermalModel.HoldPowerW(Box(), 120, 0), 2);
        Assert.Equal(44.80, ThermalModel.HoldPowerW(Box(), 120, 100), 2);
        Assert.Equal(0, ThermalModel.HoldPowerW(Box(), 200, 0));    // 200 W of electronics outweigh the 167 W loss
    }

    [Fact]
    public void Electronics_alone_settle_at_the_equilibrium_temperature()
    {
        // G·T⁴ = 200 W → T = 293.15 · (200/300)^¼ = 264.89 K (−8.3 °C)
        Assert.Equal(264.89, ThermalModel.EquilibriumK(Box(), 200, 0), 2);
        var box = new ThermalModel.WarmBox(Box(40_000));
        double heater = 0;
        for (var h = 0; h < 200; h++) heater += box.Dark(200, 0, 1);
        Assert.Equal(0, heater);
        Assert.Equal(264.89, box.TempK, 1);
    }

    [Fact]
    public void Without_thermal_mass_heaters_hold_the_set_point_from_the_first_dark_hour()
    {
        var box = new ThermalModel.WarmBox(Box(0));
        Assert.Equal(46.83, box.Dark(120, 0, 1), 2);
        Assert.Equal(253.15, box.TempK, 6);
    }

    [Fact]
    public void Thermal_mass_lets_the_box_coast_before_the_heaters_switch_on()
    {
        // 40 kJ/K: from +20 °C the box loses 300 → 167 W against 120 W of electronics, ~1.6 MJ to shed → ~3.5 h.
        var box = new ThermalModel.WarmBox(Box(40_000));
        var perHour = Enumerable.Range(0, 24).Select(_ => box.Dark(120, 0, 1)).ToArray();
        var firstOn = Array.FindIndex(perHour, w => w > 0);
        Assert.InRange(firstOn, 2, 6);
        Assert.All(perHour.Take(firstOn), w => Assert.Equal(0, w));
        Assert.Equal(46.83, perHour[^1], 2);                         // then steady on the thermostat
        // Energy balance over the cool-down: stored heat C·ΔT = ∫(loss − electronics) dt − heater energy.
        var bigger = new ThermalModel.WarmBox(Box(80_000));
        var heaterBig = Enumerable.Range(0, 24).Sum(_ => bigger.Dark(120, 0, 1));
        Assert.True(heaterBig < perHour.Sum(), "more thermal mass → less heater energy over the same night");
    }

    [Fact]
    public void A_lit_step_returns_the_box_to_its_operating_temperature()
    {
        var box = new ThermalModel.WarmBox(Box(40_000));
        for (var h = 0; h < 10; h++) box.Dark(120, 0, 1);
        Assert.True(box.TempK < 290);
        box.Lit();
        Assert.Equal(293.15, box.TempK, 6);
    }

    [Fact]
    public void Dark_endurance_with_and_without_heaters()
    {
        // battery only: 600 Wh · √0.9 / 120 W = 4.743 h
        Assert.Equal(4.743, ThermalModel.DarkEnduranceHours(600, 120, null, 0, 0.9), 3);
        // no thermal mass: 600 · √0.9 / (120 + 46.83) = 3.411 h
        Assert.Equal(3.411, ThermalModel.DarkEnduranceHours(600, 120, Box(0), 0, 0.9), 2);
        // with thermal mass the heaters start later: between the two
        var withMass = ThermalModel.DarkEnduranceHours(600, 120, Box(40_000), 0, 0.9);
        Assert.InRange(withMass, 3.42, 4.74);
    }

    // ---- Diviner lookup ----

    [Fact]
    public void Subsolar_point_from_site_elevation_and_azimuth()
    {
        // Sun due north, 5° up, seen from (−85°, 30°E): 85° along the 30°E meridian → equator.
        var (lat, lon) = DivinerPoint.SubsolarPoint(-85, 30, 5, 0);
        Assert.Equal(0, lat, 6);
        Assert.Equal(30, lon, 6);
        // Due south, 85° away: over the pole (5°) and 80° up the 210°E meridian → (−10°, 210°E).
        (lat, lon) = DivinerPoint.SubsolarPoint(-85, 30, 5, 180);
        Assert.Equal(-10, lat, 6);
        Assert.Equal(210, lon, 6);
        // At the pole the Sun's elevation is minus the subsolar latitude.
        (lat, _) = DivinerPoint.SubsolarPoint(-90, 0, 1.5, 77);
        Assert.Equal(-1.5, lat, 6);
    }

    [Fact]
    public void Curve_is_interpolated_between_bin_centres_and_picks_the_season_by_the_subsolar_latitude()
    {
        var p = new DivinerPoint
        {
            SummerK = Enumerable.Range(0, 96).Select(i => (double)i).ToArray(),
            WinterK = Enumerable.Range(0, 96).Select(i => 1000.0 + i).ToArray(),
        };
        Assert.Equal(10, p.TempK(-0.5, 10.5 * 3.75), 9);        // bin 10's centre
        Assert.Equal(10.5, p.TempK(-0.5, 11 * 3.75), 9);        // halfway to bin 11
        Assert.Equal(47.5, p.TempK(-0.5, 0), 9);                // wraps: between bin 95 (358.125°) and bin 0 (1.875°)
        Assert.Equal(1010, p.TempK(0.5, 10.5 * 3.75), 9);       // northern subsolar latitude → winter curve
    }

    // ---- In the simulator ----

    private static IlluminationSeries DayThenNight(int litHours, int darkHours, DivinerPoint? ground = null)
    {
        var n = litHours + darkHours;
        return new IlluminationSeries
        {
            SiteId = "test", Lat = -89, Lon = 0, Start = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), StepHours = 1, Count = n,
            SunFraction = Enumerable.Range(0, n).Select(i => i < litHours ? 100 : 0).ToArray(),
            SunElevCentiDeg = Enumerable.Repeat(100, n).ToArray(),
            SunAzCentiDeg = Enumerable.Repeat(0, n).ToArray(),
            EarthVisible = Enumerable.Repeat(1, n).ToArray(),
            Ground = ground,
        };
    }

    [Fact]
    public void Heaters_add_to_the_night_load_and_are_named_when_the_battery_runs_out()
    {
        var series = DayThenNight(2, 30);
        var thresholds = new MissionThresholds(0, 0);
        var plain = new LanderInputs(1000, 600, 100, 120, ParamKind.Assumption);
        var heated = plain with { Thermal = Box(0) };
        var a = MissionSimulator.Run(series, plain, series.Start, 32 / 24.0, thresholds);
        var b = MissionSimulator.Run(series, heated, series.Start, 32 / 24.0, thresholds);
        // battery only: 600 Wh / 120 W = 5 h of darkness; with 46.83 W of heaters: 600 / 166.83 = 3.6 h
        Assert.False(a.ThermalModel);
        Assert.Equal(2 + 5 - 1, a.DepletedAtStep);
        Assert.True(b.ThermalModel);
        Assert.False(b.GroundFromDiviner);                    // no Diviner data → ground at 0 K
        Assert.Equal(2 + 4 - 1, b.DepletedAtStep);
        Assert.Contains("heaters ran the last", b.Reason);
        Assert.Equal(46.83, b.HeaterPeakW, 2);
        Assert.Equal(-20, b.BoxMinTempC, 6);
        Assert.Equal(46.83, b.HeaterW[2], 2);
        Assert.Equal(0, b.HeaterW[0]);
    }

    [Fact]
    public void Warm_ground_from_Diviner_lowers_the_heater_power()
    {
        var warm = new DivinerPoint { SummerK = Enumerable.Repeat(150.0, 96).ToArray(), WinterK = Enumerable.Repeat(150.0, 96).ToArray() };
        var series = DayThenNight(2, 10, warm);
        var lander = new LanderInputs(1000, 10_000, 100, 120, ParamKind.Assumption, Thermal: Box(0));
        var r = MissionSimulator.Run(series, lander, series.Start, 12 / 24.0, new MissionThresholds(0, 0));
        Assert.True(r.GroundFromDiviner);
        Assert.Equal(150, r.GroundMinK, 9);
        // G · 0.5 · 150⁴ = 10.28 W back from the ground: 46.83 − 10.28 = 36.55 W
        Assert.InRange(r.HeaterPeakW, 36.50, 36.60);
        Assert.Equal(10 * ThermalModel.HoldPowerW(Box(0), 120, 150), r.HeaterEnergyWh, 6);
    }

    // ---- Real data ----

    private static string Data()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d!.FullName, "SeleneFrontend", "wwwroot", "data", "landers.json"))) d = d.Parent;
        return Path.Combine(d.FullName, "SeleneFrontend", "wwwroot", "data");
    }

    private static DivinerData Diviner() =>
        JsonSerializer.Deserialize<DivinerData>(File.ReadAllText(Path.Combine(Data(), "thermal", "diviner.json")), Web)!;

    private static List<LanderPreset> Landers() =>
        JsonSerializer.Deserialize<List<LanderPreset>>(File.ReadAllText(Path.Combine(Data(), "landers.json")), Web)!;

    [Fact]
    public void Every_site_and_lander_has_thermal_data()
    {
        var sites = JsonSerializer.Deserialize<List<Site>>(File.ReadAllText(Path.Combine(Data(), "sites.json")), Web)!;
        var diviner = Diviner();
        foreach (var s in sites)
        {
            var p = diviner.For(s.Id);
            Assert.NotNull(p);
            Assert.True(p!.IsValid && p.SummerK.Length == 96, s.Id);
            Assert.True(p.BinDistanceM < 170, $"{s.Id}: Diviner bin centre {p.BinDistanceM} m away (half-diagonal of 240 m is 170 m)");
            Assert.InRange(p.MinK, 25, 120);
            Assert.InRange(p.MaxK, 150, 330);
        }
        foreach (var l in Landers())
        {
            var t = l.Thermal?.ToInputs();
            Assert.NotNull(t);
            Assert.Equal(l.DayLoadW!.Value, t!.HeatLossW);           // radiators sized for the day load
            Assert.All(l.Thermal!.Params, p => Assert.False(string.IsNullOrWhiteSpace(p.Note), l.Id));
        }
    }

    [Fact]
    public void Diviner_sees_cold_ground_when_our_LOLA_horizon_says_the_site_is_dark()
    {
        // Two independent NASA data sets: Diviner's measured temperatures vs our terrain-shadow model (2027-2028).
        var diviner = Diviner();
        foreach (var p in diviner.Points.Where(p => Directory.Exists(Path.Combine(Data(), "illumination", p.Id))))
        {
            var series = IlluminationSeries.Concat(new[] { 2027, 2028 }.Select(y => JsonSerializer.Deserialize<IlluminationSeries>(
                File.ReadAllText(Path.Combine(Data(), "illumination", p.Id, $"{y}.json")), Web)!).ToList());
            series.Ground = p;
            var t = series.GroundTempK();
            var lit = Enumerable.Range(0, series.Count).Where(i => series.SunFraction[i] >= 50).Select(i => t[i]).Average();
            var dark = Enumerable.Range(0, series.Count).Where(i => series.SunFraction[i] < 50).Select(i => t[i]).Average();
            Assert.True(lit > dark + 40, $"{p.Id}: lit {lit:0} K vs dark {dark:0} K");
            Assert.InRange(dark, 40, 120);
        }
    }

    [Fact]
    public void Blue_Ghost_night_with_heaters_is_close_to_what_Mission_1_did()
    {
        // Firefly: M1 ran about 5 h after sunset before its batteries ran down. Our figures, Diviner-cold ground (70 K):
        var bg = Landers().Single(l => l.Id == "blue_ghost");
        var hours = ThermalModel.DarkEnduranceHours(bg.BatteryWh!.Value, bg.SurvivalLoadW!.Value, bg.Thermal!.ToInputs(), 70,
                                                    bg.BatteryRoundTripEff!.Value);
        var batteryOnly = ThermalModel.DarkEnduranceHours(bg.BatteryWh.Value, bg.SurvivalLoadW.Value, null, 70, bg.BatteryRoundTripEff.Value);
        Assert.InRange(hours, 3.5, 5.5);
        Assert.True(hours < batteryOnly);
    }
}
