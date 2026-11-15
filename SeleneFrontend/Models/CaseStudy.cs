namespace SeleneFrontend.Models;

/// <summary>
/// <c>data/casestudy/im2/case.json</c>, written by <c>pipeline/case_study_im2.py</c>: IM-2 Athena (March 2025),
/// planned vs actual landing site. The series files next to it use the illumination/* format but a 2025 period.
/// </summary>
public sealed class CaseStudyDoc
{
    public string Mission { get; set; } = "";
    public string LanderId { get; set; } = "";
    public DateTime LandingUtc { get; set; }
    public string LandingSource { get; set; } = "";
    public DateTime ContactEndUtc { get; set; }
    public string ContactEndSource { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public double OnSurfaceHours { get; set; }
    public string Attitude { get; set; } = "";
    public string Secondary { get; set; } = "";
    public string SecondaryUrl { get; set; } = "";
    public double OffsetM { get; set; }
    public string OffsetNote { get; set; } = "";
    public double CraterDiameterM { get; set; }
    public string CraterNote { get; set; } = "";
    public string Method { get; set; } = "";
    public Dictionary<string, CaseStudyPoint> Points { get; set; } = [];
    public CaseStudyNasa? NasaLongTerm { get; set; }
}

public sealed class CaseStudyPoint
{
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string File { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public CaseStudyWindow Window { get; set; } = new();
    public CaseStudyWindow Days14 { get; set; } = new();
}

public sealed class CaseStudyWindow
{
    public int Hours { get; set; }
    public double SunAnyPct { get; set; }
    public double SunLitPct { get; set; }
    public double EarthPct { get; set; }
    public double SunElevMinDeg { get; set; }
    public double SunElevMaxDeg { get; set; }
    public double SunClearanceMinDeg { get; set; }
    public double EarthClearanceMinDeg { get; set; }
    public double CraterFloorDarkIfDeeperThanM { get; set; }
}

public sealed class CaseStudyNasa
{
    public string Source { get; set; } = "";
    public string Url { get; set; } = "";
    public string Map { get; set; } = "";
    public Dictionary<string, Dictionary<string, CaseStudyNasaValue>> Points { get; set; } = [];
}

public sealed class CaseStudyNasaValue
{
    public double Pct { get; set; }
    public double[] RangePct { get; set; } = [];
}
