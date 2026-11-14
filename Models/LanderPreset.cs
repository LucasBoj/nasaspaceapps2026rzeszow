namespace SeleneFrontend.Models;

public class LanderPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Class { get; set; } = ""; // "clps" or "hls"
    public double? DefaultPowerW { get; set; }
    public double? DefaultBatteryWh { get; set; }
    public double? DefaultLoadW { get; set; }
    public double? DefaultMaxDarknessH { get; set; }

    public double Power { get; set; }
    public double Battery { get; set; }
    public double Load { get; set; }
    public double MaxDarkness { get; set; }
}
