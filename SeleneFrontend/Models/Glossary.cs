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
        ("score", "Score", "0–100 for each landing day: 35 × lowest battery level + 20 × share of hours with Earth in view + 10 × share of lit hours + 20 × solar surplus (energy produced beyond what the mission uses, full marks at twice the need) + 15 × terrain margin (how high the Sun and Earth stay above the terrain horizon). 0 means the battery runs empty."),
        ("clearance", "Terrain margin", "Lowest height of the Sun's and Earth's centre above the LOLA terrain horizon during the mission. A bigger margin means light and radio contact survive small errors of the 120 m terrain model. In the score: 1 − e^(−margin / 2°), averaged over Sun and Earth."),
        ("incidence", "Incidence angle", "Angle between the sunlight and the panel's perpendicular. A flat panel delivers power in proportion to its cosine (Lambert's law): full power facing the Sun, half at 60°, nothing when the Sun is behind it. At the pole the Sun is only a few degrees high and circles the whole horizon once a month, so fixed panels lose the Sun as it moves."),
        ("heading", "Landing heading", "Direction the lander faces after touchdown (degrees clockwise from lunar north). Landers can yaw before landing to aim fixed panels at the Sun. AUTO picks the heading that collects the most solar energy over the mission."),
        ("paneleff", "Panel efficiency", "Solar energy collected by the fixed panels as a share of what the same array would collect if it always faced the Sun (ideal tracking). It shows how much the incidence angle costs."),
        ("sunel", "Sun elevation", "Height of the Sun's centre above the local horizontal plane. Near the south pole it stays within a few degrees of the horizon, so terrain blocks it easily and vertical panels catch it best."),
        ("commsgap", "Comms gap", "Longest continuous stretch with Earth below the terrain horizon — no direct radio contact without a relay."),
        ("maxstay", "Max stay", "How long the battery would last if this landing (same date and heading) stayed on the surface longer than planned, capped at 30 days."),
        ("verdict", "Verdict", "FEASIBLE: the battery never runs out and stays above your limits. MARGINAL: it survives but dips below the battery limit or misses the Earth-contact target. NOT FEASIBLE: the battery runs empty."),
    ];

    public static string Text(string key) =>
        Entries.FirstOrDefault(e => e.Key == key).Text ?? key;
}
