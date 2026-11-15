namespace SeleneFrontend.Models;

public class Site
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public bool Approximate { get; set; }
    public string Source { get; set; } = "";
    public string? CandidateId { get; set; }
    public int CandidatesEvaluated { get; set; }

    /// <summary>Terrain slope (deg) from the 240 m DEM — set for custom points.</summary>
    public double? SlopeDeg { get; set; }

    public bool IsCustom => Id.StartsWith("CUSTOM", StringComparison.Ordinal);

    /// <summary>Custom point that cannot be analysed (outside 80–90°S, no terrain data): the reason; null = fine or not checked yet.</summary>
    public string? CustomUnavailable { get; set; }

    /// <summary>Custom point checked against the 240 m DEM (slope and coverage known).</summary>
    public bool CustomChecked { get; set; }
}
