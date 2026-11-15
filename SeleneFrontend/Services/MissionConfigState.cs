using SeleneFrontend.Models;
using System.Text.Json;

namespace SeleneFrontend.Services;

public class MissionConfigState
{
    // JSON files use camelCase keys; Web defaults make property matching case-insensitive.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IlluminationRepository _illumination;

    public event Action? OnStateChanged;
    public event Action<string>? OnFlyToRequested;

    public List<Site> AvailableSites { get; private set; } = [];
    public Site? SelectedSite { get; private set; }
    public bool CanFlyTo => SelectedSite is not null && !SelectedSite.Id.StartsWith("CUSTOM", StringComparison.Ordinal);
    
    public List<LanderPreset> AllLanders { get; private set; } = [];

    /// <summary>Our sunlit time vs NASA's LOLA illumination maps (optional; null if the file is missing).</summary>
    public NasaIlluminationCheck? NasaCheck { get; private set; }

    /// <summary>Permanently shadowed regions (possible water ice) near the sites (optional; null until loaded or if missing).</summary>
    public PsrScience? Psr { get; private set; }

    public NasaSiteCheck? NasaCheckFor(string? siteId) =>
        siteId is null ? null : NasaCheck?.Sites.FirstOrDefault(s => s.SiteId == siteId);
    public List<LanderPreset> FilteredLanders => AllLanders.Where(l => l.Class == SelectedLanderClass).ToList();
    public LanderPreset? SelectedLander { get; private set; }

    /// <summary>Panel height of the selected lander above the ground (m); 2 m (the precomputed series) without a value.</summary>
    public double PanelHeightM => SelectedLander?.PanelHeightM?.Value ?? PanelHeightAnalysis.SeriesHeightM;
    public string SelectedLanderClass { get; private set; } = "clps";

    // ---- What-if overrides (pitch comparisons). Data presets are never mutated. ----
    public const string WhatIfSolarArrayW = "solarArrayW";
    public const string WhatIfBatteryWh = "batteryWh";
    public const string WhatIfSurvivalLoadW = "survivalLoadW";
    public const string WhatIfHeadingDeg = "headingDeg";          // manual landing heading (absent = AUTO)
    public const string WhatIfIdealTracking = "idealTracking";    // present = ideal Sun tracking instead of fixed panels
    public const string WhatIfIdealBattery = "idealBattery";      // present = lossless battery (round-trip efficiency 1)
    public const string WhatIfHeatersOff = "heatersOff";          // present = no heater model (survival load only)

    private readonly Dictionary<string, double> _whatIf = [];

    public bool HasWhatIf => _whatIf.Count > 0;
    public bool IsWhatIf(string key) => _whatIf.ContainsKey(key);

    /// <summary>Manual landing heading in degrees, or null for AUTO (best heading for each mission).</summary>
    public double? HeadingOverrideDeg => _whatIf.TryGetValue(WhatIfHeadingDeg, out var h) ? h : null;

    /// <summary>What-if: simulate ideal Sun tracking instead of the lander's fixed panels.</summary>
    public bool IdealTracking => _whatIf.ContainsKey(WhatIfIdealTracking);

    /// <summary>What-if: lossless battery instead of the lander's round-trip efficiency.</summary>
    public bool IdealBattery => _whatIf.ContainsKey(WhatIfIdealBattery);

    /// <summary>What-if: battery only, no heaters at night (the pre-thermal model; an optimistic bound).</summary>
    public bool HeatersOff => _whatIf.ContainsKey(WhatIfHeatersOff);

    /// <summary>The selected lander has a heater (thermal) model in landers.json.</summary>
    public bool HasThermalModel => SelectedLander?.Thermal?.ToInputs() is not null;

    /// <summary>Battery round-trip efficiency used by calculations (1 for the ideal-battery what-if or when the preset has none).</summary>
    public double BatteryRoundTripEff => IdealBattery ? 1.0 : SelectedLander?.BatteryRoundTripEff?.Value ?? 1.0;

    /// <summary>The selected lander has fixed body-mounted panels (heading and incidence matter).</summary>
    public bool HasFixedPanels => SelectedLander?.Panels is { IsFixed: true };

    public void ClearWhatIf(string key)
    {
        if (_whatIf.Remove(key)) OnStateChanged?.Invoke();
    }

    /// <summary>Effective value used by calculations: what-if override if set, otherwise the preset figure.</summary>
    public double? Effective(string key)
    {
        if (_whatIf.TryGetValue(key, out var v)) return v;
        return key switch
        {
            WhatIfSolarArrayW => SelectedLander?.SolarArrayW?.Value,
            WhatIfBatteryWh => SelectedLander?.BatteryWh?.Value,
            WhatIfSurvivalLoadW => SelectedLander?.SurvivalLoadW?.Value,
            _ => null,
        };
    }

    public void SetWhatIf(string key, double value)
    {
        _whatIf[key] = value;
        OnStateChanged?.Invoke();
    }

    public void ResetWhatIf()
    {
        if (_whatIf.Count == 0) return;
        _whatIf.Clear();
        OnStateChanged?.Invoke();
    }
    
    // Landing-date window (UTC dates). Set to the precomputed data range once it is known.
    public DateTime WindowStart { get; private set; } = DateTime.UtcNow.Date;
    public DateTime WindowEnd { get; private set; } = DateTime.UtcNow.Date.AddYears(2);

    /// <summary>First / last day with precomputed illumination data (UTC).</summary>
    public DateTime? DataStart { get; private set; }
    public DateTime? DataEnd { get; private set; }

    /// <summary>Shown under the window fields when the user picked dates outside the data.</summary>
    public string? WindowNotice { get; private set; }

    /// <summary>
    /// Bumped on every window edit. Used as @key on the date inputs: when a typed date is
    /// clipped back to the same value, Blazor sees no attribute change and would leave the
    /// out-of-range text in the field.
    /// </summary>
    public int WindowRevision { get; private set; }

    public string DataRangeText => DataStart is { } a && DataEnd is { } b
        ? $"data available {a:yyyy-MM-dd} … {b:yyyy-MM-dd}" : "";
    public double MissionDurationDays { get; private set; } = 7;
    public double TimeStepHours { get; private set; } = 1;
    
    public double MinCommsPercent { get; private set; } = 50;
    public double MinBatteryPercent { get; private set; } = 20;

    public MissionConfigState(HttpClient httpClient, IlluminationRepository illumination)
    {
        _httpClient = httpClient;
        _illumination = illumination;
        _illumination.PanelHeightProvider = () => PanelHeightM;
    }

    /// <summary>Catalog loading state for the status bar / error banner.</summary>
    public bool IsLoaded { get; private set; }
    public string? LoadError { get; private set; }

    private async Task LoadPsrAsync()
    {
        try
        {
            var json = await _httpClient.GetStringAsync("data/science/psr.json");
            Psr = JsonSerializer.Deserialize<PsrScience>(json, JsonOpts);
            OnStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MissionConfigState] PSR science layer unavailable: {ex.Message}");
        }
    }

    public async Task LoadData()
    {
        LoadError = null;
        try
        {
            var sitesJson = await _httpClient.GetStringAsync("data/sites.json");
            AvailableSites = JsonSerializer.Deserialize<List<Site>>(sitesJson, JsonOpts) ?? [];

            var landersJson = await _httpClient.GetStringAsync("data/landers.json");
            AllLanders = JsonSerializer.Deserialize<List<LanderPreset>>(landersJson, JsonOpts) ?? [];
            
            try
            {
                var (dataStart, dataEnd) = await _illumination.GetRangeAsync();
                // Landing dates from today (not the past) to the end of the data (2035-12-31).
                var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
                DataStart = dataStart.Date > today ? dataStart.Date : today;
                DataEnd = dataEnd.Date;
                (WindowStart, WindowEnd) = DefaultWindow();
                _illumination.SetWindow(WindowStart, WindowEnd);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MissionConfigState] Illumination index unavailable: {ex.Message}");
            }

            try
            {
                var checkJson = await _httpClient.GetStringAsync("data/validation/nasa_illumination.json");
                NasaCheck = JsonSerializer.Deserialize<NasaIlluminationCheck>(checkJson, JsonOpts);
            }
            catch (Exception ex)
            {
                // Optional validation data; the app works without it.
                Console.Error.WriteLine($"[MissionConfigState] NASA illumination check unavailable: {ex.Message}");
            }

            if (AvailableSites.Count > 0)
            {
                SelectedSite = AvailableSites[0];
            }
            _ = LoadPsrAsync();      // optional science layer, not awaited: it must not delay the start
            
            IsLoaded = AvailableSites.Count > 0 && AllLanders.Count > 0;
            if (!IsLoaded) LoadError = "site or lander catalog is empty";
            OnStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MissionConfigState] Catalog load failed: {ex.Message}");
            LoadError = $"could not load the site/lander catalog ({ex.Message})";
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>The last point clicked on the terrain (80–90°S analysis), kept so the heatmap row can select it again.</summary>
    public Site? CustomSite { get; private set; }

    public async Task SelectSite(string siteId, Site? customSite = null, bool fly = true)
    {
        if (customSite != null)
        {
            SelectedSite = customSite;
            if (customSite.IsCustom) CustomSite = customSite;
        }
        else
        {
            SelectedSite = AvailableSites.FirstOrDefault(s => s.Id == siteId)
                           ?? (CustomSite is { } c && c.Id == siteId ? c : null);
        }
        OnStateChanged?.Invoke();
        // Every explicit site selection (map pin or list) flies the camera there,
        // except programmatic selections during the intro (the camera is doing its own flight).
        if (fly) RequestFlyTo();
        await Task.CompletedTask;
    }

    /// <summary>The selected site's details changed (e.g. a custom point's slope arrived): re-render.</summary>
    public void NotifySiteChanged() => OnStateChanged?.Invoke();

    public void RequestFlyTo()
    {
        if (CanFlyTo)
        {
            OnFlyToRequested?.Invoke(SelectedSite!.Id);
        }
    }

    public async Task SelectLanderClass(string landerClass)
    {
        SelectedLanderClass = landerClass;
        SelectedLander = null;
        _whatIf.Clear();
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async Task SelectLander(string landerId)
    {
        SelectedLander = AllLanders.FirstOrDefault(l => l.Id == landerId);
        _whatIf.Clear();
        if (SelectedLander?.MissionDurationDays is { } duration)
        {
            MissionDurationDays = duration.Value;
        }
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    /// <summary>Longest landing window the analysis accepts (performance and heatmap readability).</summary>
    public const int MaxWindowMonths = 24;

    /// <summary>Default window: 2027–2028 (unchanged), clipped to the data.</summary>
    public (DateTime Start, DateTime End) DefaultWindow()
    {
        var a = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var b = new DateTime(2028, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        if (DataStart is { } ds && a < ds) a = ds;
        if (DataEnd is { } de && b > de) b = de;
        return (a, b < a ? a : b);
    }

    public async Task UpdateWindowStart(DateTime date)
    {
        WindowStart = Clip(TimeUtil.AsUtc(date).Date);
        if (WindowEnd < WindowStart) WindowEnd = WindowStart;
        if (WindowEnd > WindowStart.AddMonths(MaxWindowMonths).AddDays(-1))
        {
            WindowEnd = WindowStart.AddMonths(MaxWindowMonths).AddDays(-1);
            WindowNotice = $"the landing window is limited to {MaxWindowMonths} months — the latest date moved to {WindowEnd:yyyy-MM-dd}";
        }
        WindowChanged();
        await Task.CompletedTask;
    }

    public async Task UpdateWindowEnd(DateTime date)
    {
        WindowEnd = Clip(TimeUtil.AsUtc(date).Date);
        if (WindowStart > WindowEnd) WindowStart = WindowEnd;
        if (WindowStart < WindowEnd.AddMonths(-MaxWindowMonths).AddDays(1))
        {
            WindowStart = WindowEnd.AddMonths(-MaxWindowMonths).AddDays(1);
            WindowNotice = $"the landing window is limited to {MaxWindowMonths} months — the earliest date moved to {WindowStart:yyyy-MM-dd}";
        }
        WindowChanged();
        await Task.CompletedTask;
    }

    /// <summary>Set both ends at once (calendar shortcuts); same limits as typing the dates.</summary>
    public async Task SetWindow(DateTime start, DateTime end)
    {
        WindowStart = Clip(TimeUtil.AsUtc(start).Date);
        var notice = WindowNotice;
        WindowEnd = Clip(TimeUtil.AsUtc(end).Date);
        WindowNotice ??= notice;
        if (WindowEnd < WindowStart) WindowEnd = WindowStart;
        if (WindowEnd > WindowStart.AddMonths(MaxWindowMonths).AddDays(-1))
        {
            WindowEnd = WindowStart.AddMonths(MaxWindowMonths).AddDays(-1);
            WindowNotice = $"the landing window is limited to {MaxWindowMonths} months";
        }
        WindowChanged();
        await Task.CompletedTask;
    }

    private void WindowChanged()
    {
        WindowRevision++;
        _illumination.SetWindow(WindowStart, WindowEnd);
        OnStateChanged?.Invoke();
    }

    /// <summary>Clamp a date to the data range and remember a notice when clipping happened.</summary>
    private DateTime Clip(DateTime date)
    {
        WindowNotice = null;
        if (DataStart is { } a && date < a) { WindowNotice = DataRangeText; return a; }
        if (DataEnd is { } b && date > b) { WindowNotice = DataRangeText; return b; }
        return DateTime.SpecifyKind(date, DateTimeKind.Utc);
    }

    public async Task UpdateMissionDuration(double days)
    {
        MissionDurationDays = days;
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async Task UpdateTimeStep(double hours)
    {
        TimeStepHours = hours;
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async Task UpdateMinComms(double percent)
    {
        MinCommsPercent = percent;
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public async Task UpdateMinBattery(double percent)
    {
        MinBatteryPercent = percent;
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public void Reset()
    {
        (WindowStart, WindowEnd) = DefaultWindow();
        _illumination.SetWindow(WindowStart, WindowEnd);
        WindowNotice = null;
        MissionDurationDays = 7;
        TimeStepHours = 1;
        MinCommsPercent = 50;
        MinBatteryPercent = 20;
        SelectedLander = null;
        _whatIf.Clear();
        OnStateChanged?.Invoke();
    }
}
