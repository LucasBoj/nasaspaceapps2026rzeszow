namespace SeleneFrontend.Models;

/// <summary>Provenance of a lander figure.</summary>
public static class ParamKind
{
    public const string Sourced = "sourced";       // published by the manufacturer / NASA / press
    public const string Derived = "derived";       // computed from sourced figures
    public const string Assumption = "assumption"; // class estimate, no public figure
    public const string WhatIf = "whatif";         // user's what-if override (pitch comparisons)

    /// <summary>Weakest provenance among inputs: assumption &lt; derived &lt; sourced.</summary>
    public static string Weakest(params LanderParam?[] inputs)
    {
        var kinds = inputs.OfType<LanderParam>().Select(p => p.Kind).ToList();
        if (kinds.Contains(Assumption)) return Assumption;
        if (kinds.Contains(Derived)) return Derived;
        return Sourced;
    }

    public static string Label(string kind) => kind switch
    {
        Sourced => "SOURCED",
        Derived => "DERIVED",
        WhatIf => "WHAT-IF",
        _ => "ASSUMPTION",
    };
}

/// <summary>Common metadata of a lander parameter.</summary>
public abstract class LanderParam
{
    public string Unit { get; set; } = "";
    public string Kind { get; set; } = ParamKind.Assumption;
    public string Note { get; set; } = "";
    public string SourceUrl { get; set; } = "";

    public bool IsAssumption => Kind == ParamKind.Assumption;
}

public sealed class NumberParam : LanderParam
{
    public double Value { get; set; }
}

public sealed class BoolParam : LanderParam
{
    public bool Value { get; set; }
}

/// <summary>One flat solar panel fixed to the lander body (no tracking mechanism).</summary>
public sealed class PanelFacet
{
    public string Name { get; set; } = "";

    /// <summary>Azimuth of the panel normal relative to the lander heading, degrees clockwise (seen from above).</summary>
    public double AzOffsetDeg { get; set; }

    /// <summary>Elevation of the panel normal: 0 = vertical panel facing the horizon, 90 = top-deck panel facing up.</summary>
    public double NormalElevDeg { get; set; }

    /// <summary>Relative share of the array output; shares are normalised to sum to 1.</summary>
    public double Share { get; set; } = 1;
}

/// <summary>
/// How the solar array is mounted. <c>fixed</c>: body-mounted facets, output follows the Lambert cosine of the
/// Sun's incidence angle on each facet. <c>ideal</c>: the array always faces the Sun (upper bound, the pre-2026-11-14 model).
/// </summary>
public sealed class PanelLayout : LanderParam
{
    public const string FixedMode = "fixed";
    public const string IdealMode = "ideal";

    public string Mode { get; set; } = IdealMode;
    public List<PanelFacet> Facets { get; set; } = [];

    public bool IsFixed => Mode == FixedMode && Facets.Count > 0 && Facets.Sum(f => Math.Max(0, f.Share)) > 0;
}

/// <summary>A read-only lander preset; every figure carries its provenance.</summary>
public class LanderPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Class { get; set; } = ""; // "clps" or "hls"

    public NumberParam? SolarArrayW { get; set; }
    public NumberParam? BatteryWh { get; set; }
    public NumberParam? DayLoadW { get; set; }
    public NumberParam? SurvivalLoadW { get; set; }
    public NumberParam? MissionDurationDays { get; set; }
    public BoolParam? DesignedForNight { get; set; }
    public NumberParam? Crew { get; set; }
    public NumberParam? PayloadKg { get; set; }

    /// <summary>Solar array mounting (fixed facets or ideal tracking); null = ideal tracking.</summary>
    public PanelLayout? Panels { get; set; }

    /// <summary>Optional glTF/GLB model for the 3D view (visualisation only; falls back to primitives).</summary>
    public string? ModelUri { get; set; }

    /// <summary>Height the 3D model is scaled to, in metres (visual scale, not a provenance-tracked figure).</summary>
    public double? ModelHeightM { get; set; }

    /// <summary>All present parameters, in display order.</summary>
    public IEnumerable<LanderParam> AllParams =>
        new LanderParam?[] { SolarArrayW, Panels, BatteryWh, DayLoadW, SurvivalLoadW, MissionDurationDays, DesignedForNight, Crew, PayloadKg }
            .OfType<LanderParam>();

    public int CountKind(string kind) => AllParams.Count(p => p.Kind == kind);
}
