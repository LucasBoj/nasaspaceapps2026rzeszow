namespace SeleneFrontend.Models;

/// <summary>Plain-language explanations shown in tooltips and in the help sheet.</summary>
public static class Glossary
{
    public static readonly IReadOnlyList<(string Key, string Term, string Text)> Entries =
    [
        ("clps", "CLPS", "Commercial Lunar Payload Services — NASA buys rides to the Moon for robotic payloads on commercial landers such as Nova-C, Blue Ghost and Griffin."),
        ("hls", "HLS", "Human Landing System — the crewed landers NASA contracted for Artemis: SpaceX Starship HLS and Blue Origin Blue Moon."),
        ("soc", "SoC", "State of charge — how full the lander's battery is, as a percentage of its capacity."),
        ("psr", "PSR", "Permanently shadowed region — crater floors near the poles that never see the Sun. They are extremely cold and may trap water ice."),
        ("earth", "Earth visibility", "Earth is above the local terrain horizon (with a 0.5° margin), so direct radio contact is geometrically possible. It does not guarantee that a ground station is in view."),
        ("lit", "Sunlit hour", "An hour counts as lit when at least half of the Sun's disk is above the terrain horizon. Solar panels then deliver power and the lander runs its daytime load."),
        ("score", "Score", "0–100 for each landing day: 50 × lowest battery level + 30 × share of hours with Earth in view + 20 × share of lit hours. 0 means the battery runs empty."),
        ("verdict", "Verdict", "FEASIBLE: the battery never runs out and stays above your limits. MARGINAL: it survives but dips below the battery limit or misses the Earth-contact target. NOT FEASIBLE: the battery runs empty."),
    ];

    public static string Text(string key) =>
        Entries.FirstOrDefault(e => e.Key == key).Text ?? key;
}
