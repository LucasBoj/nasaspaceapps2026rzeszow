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
}
