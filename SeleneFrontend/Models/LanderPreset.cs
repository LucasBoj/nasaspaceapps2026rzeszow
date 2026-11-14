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

    /// <summary>All present parameters, in display order.</summary>
    public IEnumerable<LanderParam> AllParams =>
        new LanderParam?[] { SolarArrayW, BatteryWh, DayLoadW, SurvivalLoadW, MissionDurationDays, DesignedForNight, Crew, PayloadKg }
            .OfType<LanderParam>();

    public int CountKind(string kind) => AllParams.Count(p => p.Kind == kind);
}
